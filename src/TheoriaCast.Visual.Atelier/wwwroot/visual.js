import { bindKaraoke, showKaraokeJobs } from "./karaoke.js";

const $ = id => document.getElementById(id);
const form = $("edit-form");
const selected = new Map();
const assetsById = new Map();
const cutDrafts = new Map();
const prefix = "/api/v1/visual";
const padStyles = [["solid", "無地"], ["edge", "縁の平均色"], ["blur", "画像をぼかす"]];
const marginSides = [["left", "左"], ["top", "上"], ["right", "右"], ["bottom", "下"]];
const resizePresets = [["1920,1080", "横長 MV（1920 × 1080）"], ["1080,1920", "縦長ショート（1080 × 1920）"],
  ["1200,630", "記事（1200 × 630）"], ["1080,1080", "正方形（1080 × 1080）"]];
const touches = [["illustration", "イラスト"], ["manga", "漫画（CPUでは鉛筆画）"], ["pencil", "鉛筆画"], ["detail", "細密画"]];
const jobLabels = { queued: "待機中", running: "処理中", done: "完了", partial: "一部完了", failed: "失敗" };
const decisionLabels = { pending: "未判定", accepted: "採用", rejected: "見送り" };
const hardwareLabels = { cpu: "ローカル CPU", gpu: "GPU", "cloud-gpu": "クラウド GPU" };
const connectionLabels = { online: "接続中", unavailable: "確認不可", unauthorized: "認証エラー", mismatch: "識別子が不一致", checking: "接続を確認中" };
let server = null;
let panel = "assets";
let currentSet = null;
let generation = 0;
let busy = false;
let loading = false;
let cosmosInspection = false;
let matteEdgeRefinement = false;
let assetQuery = "";

function element(tag, text, className) {
  const node = document.createElement(tag);
  if (text !== undefined) node.textContent = text;
  if (className) node.className = className;
  return node;
}

function button(text, action, className = "quiet") {
  const node = element("button", text, className);
  node.type = "button";
  node.addEventListener("click", action);
  return node;
}

function setMessage(message, error = false) {
  const status = $("message");
  status.textContent = message;
  status.classList.toggle("error", error);
  status.hidden = !message;
}

async function ask(path, { body, method = body === undefined ? "GET" : "POST", pinned = server, api = true } = {}) {
  if (api && !pinned) throw new Error("素材サーバーの接続ファイルを読み込んでください。");
  const headers = api ? { "X-Theoria-Server": pinned } : {};
  if (body !== undefined) headers["Content-Type"] = "application/json";
  const response = await fetch((api ? prefix : "") + path, {
    method, headers, body: body === undefined ? undefined : JSON.stringify(body), cache: "no-store"
  });
  if (!response.ok) {
    const detail = await response.json().catch(() => null);
    throw new Error(detail?.error || "素材サーバーで要求を完了できませんでした。");
  }
  return response.json();
}

async function action(callback) {
  if (busy) return;
  busy = true;
  const inputs = ["connection-file", "server", "files", "folder"].map($);
  for (const input of inputs) input.disabled = true;
  setMessage("");
  $("recipe-error").hidden = true;
  $("work-error").hidden = true;
  try { await callback(); }
  catch (error) {
    const issue = $("recipe-dialog").open ? $("recipe-error") : $("work-dialog").open ? $("work-error") : null;
    if (issue) {
      issue.textContent = error.message;
      issue.hidden = false;
    } else setMessage(error.message, true);
  }
  finally {
    for (const input of inputs) input.disabled = false;
    busy = false;
  }
}

function assetPath(ref) { return "/asset-refs/" + [ref.serverId, ref.assetId, ref.sha256].map(encodeURIComponent).join("/"); }
function imageUrl(ref) { return prefix + assetPath(ref) + "/image?server=" + encodeURIComponent(server); }

function preview(ref, name) {
  const img = element("img", undefined, "preview");
  img.src = imageUrl(ref);
  img.alt = name;
  img.loading = "lazy";
  return img;
}

function empty(text) { return element("p", text, "empty"); }

function selectField(name, options, label, value) {
  const select = document.createElement("select");
  select.name = name;
  for (const [id, text] of options) select.add(new Option(text, id));
  if (value !== undefined) select.value = value;
  const field = element("label", label);
  field.append(select);
  return field;
}

function inputField(type, name, value, label, attrs = {}) {
  const input = document.createElement("input");
  Object.assign(input, { type, name, value }, attrs);
  const field = element("label", label);
  field.append(input);
  return field;
}

const matteFields = {
  maskRefineRadius: { label: "輪郭に合わせる半径（px）", max: 32 },
  maskErodePx: { label: "マスクを内側へ縮める幅（px）", max: 2 },
  decontaminateEdge: { label: "縁に残る背景色の除去" }
};

function matteField(parameter, name, value) {
  const { label, max } = matteFields[parameter];
  return max === undefined
    ? selectField(name, [["false", "しない"], ["true", "する"]], label, String(value))
    : inputField("number", name, value, label, { min: 0, max, step: 1, required: true });
}

function showWork(title, content) {
  $("work-title").textContent = title;
  $("work-detail").replaceChildren(content);
  $("work-dialog").showModal();
}

function renderAdjustments() {
  const fields = form.elements;
  const root = $("adjustments");
  const operation = fields.operation.value;
  root.replaceChildren();
  if (operation === "stylize") {
    root.append(selectField("touch", touches, "タッチ"),
      inputField("number", "strength", 80, "強さ（%）", { min: 0, max: 100, required: true }));
  } else if (operation === "resize") {
    const options = [...resizePresets, ["custom", "カスタム"]];
    root.append(selectField("preset", options, "規格"));
    const sizes = element("div", undefined, "pair");
    sizes.hidden = true;
    sizes.append(inputField("number", "width", 1920, "幅（px）", { min: 1, max: 8192, required: true }),
      inputField("number", "height", 1080, "高さ（px）", { min: 1, max: 8192, required: true }));
    root.append(sizes, selectField("fitMode", [["crop", "中央を切り抜く"], ["pad", "余白を足す"]], "収め方"));
    fields.preset.addEventListener("change", () => { sizes.hidden = fields.preset.value !== "custom"; });
    const padding = element("fieldset");
    padding.append(selectField("padStyle", padStyles, "余白の背景"), inputField("color", "padColor", "#f5f2eb", "余白色"));
    const margins = element("div", undefined, "pair");
    margins.append(...marginSides.map(([name, label]) => inputField("number", name, 0, label,
      { min: 0, max: 99, step: "0.1", required: true })));
    const safeArea = element("fieldset");
    safeArea.append(element("legend", "安全余白（%）"), margins);
    padding.append(safeArea);
    root.append(padding);
    const syncPadding = () => {
      padding.hidden = padding.disabled = fields.fitMode.value !== "pad";
      fields.padColor.parentElement.hidden = fields.padStyle.value !== "solid";
    };
    fields.fitMode.addEventListener("change", syncPadding);
    fields.padStyle.addEventListener("change", syncPadding);
    syncPadding();
  } else if (operation === "colorMatch") {
    root.append(selectField("referenceAsset", [], "基準画像"),
      inputField("number", "colorMatchStrength", 100, "補正の強さ（%）", { min: 0, max: 100, required: true }));
    updateReferences().catch(error => setMessage(error.message, true));
  } else if (operation === "inspect") {
    const modes = [["pixels", "画素検査"]];
    if (cosmosInspection) modes.push(["cosmos", "画素検査と Cosmos タグ"]);
    root.append(selectField("inspectionMode", modes, "検品方法"));
  } else {
    root.append(selectField("background", [["transparent", "透明"], ["solid", "無地"], ["image", "手持ち画像"], ["generated", "生成背景"]], "背景"));
    const additional = element("div");
    root.append(additional);
    const backgroundOptions = () => {
      additional.replaceChildren();
      if (fields.background.value === "solid") additional.append(inputField("color", "color", "#b7c5ab", "背景色"));
      if (fields.background.value === "image") {
        const choices = [["", "素材を選択"], ...Array.from(assetsById.values(), asset => [asset.reference.assetId, asset.name])];
        additional.append(selectField("backgroundAsset", choices, "背景に使う素材"));
        fields.backgroundAsset.required = true;
      }
      if (fields.background.value === "generated")
        additional.append(inputField("text", "prompt", "", "背景の生成指示", { maxLength: 1000, required: true }));
      updateModelOptions();
    };
    fields.background.addEventListener("change", backgroundOptions);
    backgroundOptions();
    if (matteEdgeRefinement) {
      const edges = element("fieldset");
      const widths = element("div", undefined, "pair");
      widths.append(matteField("maskRefineRadius", "maskRefineRadius", 0), matteField("maskErodePx", "maskErodePx", 0));
      edges.append(element("legend", "背景のエッジ補正"), widths,
        matteField("decontaminateEdge", "decontaminateEdge", false));
      root.append(edges);
    }
  }
  updateModelOptions();
  $("estimate").hidden = true;
}

async function updateReferences() {
  const fields = form.elements;
  const picker = fields.referenceAsset;
  if (!picker || !server) return;
  const pinned = server, setId = fields.set.value, previous = picker.value;
  const assets = setId ? (await ask("/sets/" + setId, { pinned })).items
    .filter(item => item.availability === "available").map(item => ({ reference: item.asset, name: item.name }))
    : [...assetsById.values()];
  if (server !== pinned || fields.set.value !== setId || picker !== fields.referenceAsset) return;
  picker.replaceChildren(new Option("素材を選択", ""));
  for (const asset of assets) {
    const option = new Option(asset.name, asset.reference.serverId + ":" + asset.reference.assetId);
    option.dataset.reference = JSON.stringify(asset.reference);
    picker.add(option);
  }
  if ([...picker.options].some(option => option.value === previous)) picker.value = previous;
  picker.required = true;
}

function updateModelOptions() {
  const fields = form.elements;
  const gpu = ["gpu", "cloud-gpu"].includes(fields.hardware.value);
  const needsModel = fields.operation.value === "stylize" && gpu || fields.background?.value === "generated";
  $("model-options").hidden = !needsModel;
  $("model-options").disabled = !needsModel;
  fields.hardware.disabled = ["resize", "colorMatch", "inspect"].includes(fields.operation.value)
    || fields.operation.value === "background" && fields.background?.value !== "generated";
}

function batch() {
  if (!form.reportValidity()) throw new Error("編集パラメータを確認してください。");
  const fields = form.elements;
  const settings = { operation: fields.operation.value };
  let hardware = fields.hardware.value;
  if (settings.operation === "stylize") {
    settings.touch = fields.touch.value;
    settings.strength = Number(fields.strength.value) / 100;
  } else if (settings.operation === "resize") {
    hardware = "cpu";
    const [width, height] = fields.preset.value === "custom"
      ? [Number(fields.width.value), Number(fields.height.value)] : fields.preset.value.split(",").map(Number);
    Object.assign(settings, { width, height, fitMode: fields.fitMode.value });
    if (settings.fitMode === "pad") {
      settings.padStyle = fields.padStyle.value;
      if (settings.padStyle === "solid") settings.padColor = fields.padColor.value;
      settings.marginOffsets = Object.fromEntries(marginSides.map(([name]) => [name, Number(fields[name].value) / 100]));
    }
  } else if (settings.operation === "colorMatch") {
    hardware = "cpu";
    const reference = fields.referenceAsset.selectedOptions[0]?.dataset.reference;
    if (!reference) throw new Error("基準画像を選択してください。");
    settings.referenceAsset = JSON.parse(reference);
    settings.colorMatchStrength = Number(fields.colorMatchStrength.value) / 100;
  } else if (settings.operation === "inspect") {
    hardware = "cpu";
    settings.inspectionMode = fields.inspectionMode.value;
  } else {
    settings.background = fields.background.value;
    if (settings.background !== "generated") hardware = "cpu";
    if (settings.background === "solid") settings.color = fields.color.value;
    if (settings.background === "image") settings.backgroundAsset = assetsById.get(fields.backgroundAsset.value)?.reference;
    if (settings.background === "generated") settings.prompt = fields.prompt.value;
    if (fields.maskRefineRadius) {
      settings.maskRefineRadius = fields.maskRefineRadius.valueAsNumber;
      settings.maskErodePx = fields.maskErodePx.valueAsNumber;
      settings.decontaminateEdge = fields.decontaminateEdge.value === "true";
    }
  }
  if (!$("model-options").disabled) {
    settings.resolution = Number(fields.resolution.value);
    settings.seed = Number(fields.seed.value);
  }
  const setId = fields.set.value;
  const inputs = [...selected.values()];
  if (!setId && !inputs.length) throw new Error("素材棚で画像を選ぶか、素材セットを指定してください。");
  return { settings, hardware, ...(setId ? { setId } : { inputs }) };
}

function showEstimate(estimate) {
  const target = $("estimate");
  const duration = estimate.batchMinutes < 1 ? Math.max(1, Math.round(estimate.batchMinutes * 60)) + "秒" : estimate.batchMinutes + "分";
  target.replaceChildren(
    element("strong", hardwareLabels[estimate.hardware] + " ・ " + estimate.model),
    element("div", "合計 約" + duration + " ／ 一晩（8時間）で約" + estimate.imagesPerNight.toLocaleString("ja-JP") + "件"));
  if (estimate.exceedsNight) target.append(element("div", "一晩では完了しない見込みです。 " + estimate.alternative));
  target.hidden = false;
}

function updateSelection() {
  $("selection").textContent = selected.size + " 件選択";
  $("create-set").disabled = selected.size === 0;
  $("estimate").hidden = true;
}

function renderAssets(assets, shown = assets) {
  assetsById.clear();
  for (const asset of assets) assetsById.set(asset.reference.assetId, asset);
  const root = $("asset-grid");
  root.replaceChildren();
  for (const asset of shown) {
    const ref = asset.reference;
    const card = element("article", undefined, "asset");
    const controls = element("div", undefined, "asset-body");
    const check = document.createElement("input");
    check.type = "checkbox";
    check.checked = selected.has(ref.assetId);
    card.classList.toggle("selected", check.checked);
    check.addEventListener("change", () => {
      if (check.checked) selected.set(ref.assetId, ref);
      else selected.delete(ref.assetId);
      card.classList.toggle("selected", check.checked);
      updateSelection();
    });
    const title = element("label");
    title.append(check, document.createTextNode(asset.name));
    const links = element("div", undefined, "buttons");
    const save = element("a", "保存", "button quiet");
    save.href = imageUrl(ref);
    save.download = asset.name;
    links.append(save);
    if (asset.recipeId) links.append(button("レシピ", () => action(() => openRecipe(asset.recipeId))));
    controls.append(title, element("small", asset.width + " × " + asset.height), links);
    if (asset.inspection) {
      controls.append(element("small", asset.inspection.tags.join(" ・ ")));
      links.append(button(asset.inspection.flags.length ? "要確認 " + asset.inspection.flags.length + " 件" : "検品結果",
        () => openInspection(asset.inspection)));
    }
    card.append(preview(ref, asset.name), controls);
    root.append(card);
  }
  if (!shown.length) root.append(empty(assetQuery ? "検索条件に一致する素材がありません。" : "PNG または JPEG を取り込むと、ここに表示されます。"));
  if (form.elements.background?.value === "image") {
    const picker = form.elements.backgroundAsset;
    const old = picker.value;
    picker.replaceChildren(new Option("素材を選択", ""));
    for (const asset of assets) picker.add(new Option(asset.name, asset.reference.assetId));
    picker.value = old;
  }
  $("selection").textContent = selected.size + " 件選択";
}

function openInspection(inspection) {
  const content = element("div");
  content.append(element("p", inspection.tags.join(" ・ ")));
  if (inspection.sourceAsset) {
    const comparison = element("div", undefined, "inspection-comparison");
    for (const [ref, label] of [[inspection.sourceAsset, "元画像"], [inspection.asset, "検品対象"]]) {
      const figure = element("figure", undefined, "asset");
      const image = preview(ref, label);
      image.addEventListener("error", () => image.replaceWith(empty("画像を表示できません。")), { once: true });
      figure.append(image, element("figcaption", label, "asset-body"));
      comparison.append(figure);
    }
    content.append(comparison);
  }
  if (inspection.flags.length) {
    const list = element("ul");
    for (const flag of inspection.flags) list.append(element("li", flag.reason));
    content.append(list);
  } else content.append(element("p", "要確認フラグなし"));
  if (inspection.modelLicense) {
    const link = element("a", inspection.modelLicense.file + " ・ " + inspection.modelLicense.license, "button quiet");
    link.href = inspection.modelLicense.source;
    content.append(link);
  }
  content.append(button("検品レシピ", () => action(async () => {
    $("work-dialog").close();
    await openRecipe(inspection.recipeId);
  })));
  showWork("検品結果", content);
}

function renderJobs(jobs) {
  const root = $("job-list");
  const open = new Set(Array.from(root.querySelectorAll("details[open]"), node => node.dataset.id));
  root.replaceChildren();
  for (const job of jobs) {
    const details = element("details");
    details.dataset.id = job.id;
    details.open = open.has(job.id);
    const successes = job.items.filter(item => item.status === "done");
    const failures = job.items.filter(item => item.status === "failed");
    const title = [new Date(job.createdAt).toLocaleString("ja-JP"), jobLabels[job.status],
      (successes.length + failures.length) + "/" + job.items.length + " 件"].join(" ・ ");
    details.append(element("summary", title));
    const body = element("div", undefined, "job-details");
    body.append(element("p", hardwareLabels[job.estimate.hardware] + " ・ " + job.estimate.model));
    if (successes.length && job.kind === "previewSweep" && ["done", "partial"].includes(job.status))
      body.append(button("コンタクトシート", () => action(() => openSweepResults(job))));
    if (successes.some(item => item.output) && ["done", "partial"].includes(job.status))
      body.append(button("結果を素材セットで確認", () => action(async () => {
        const set = await ask("/sets", { body: { name: new Date(job.createdAt).toLocaleString("ja-JP") + " の処理結果", jobId: job.id } });
        currentSet = set.id;
        await switchPanel("sets");
      })));
    const results = element("div", undefined, "buttons");
    successes.forEach((item, index) => results.append(item.inspection
      ? button("検品結果 " + (index + 1), () => openInspection(item.inspection))
      : button("結果 " + (index + 1) + " のレシピ", () => action(() => openRecipe(item.recipeId)))));
    body.append(results);
    failures.forEach(item => body.append(element("p", item.error, "error-text")));
    details.append(body);
    root.append(details);
  }
  if (!jobs.length) root.append(empty("一括編集で登録したジョブの進行状況を確認できます。"));
}

function renderSetList(sets) {
  const root = $("set-list");
  const picker = form.elements.set;
  const chosen = picker.value;
  picker.replaceChildren(new Option("素材棚の選択", ""));
  root.replaceChildren();
  for (const set of sets) {
    picker.add(new Option(set.name, set.id));
    const row = element("article", undefined, "set-row");
    const name = element("div");
    name.append(element("h3", set.name), element("small", set.items.length + " 件 ・ " + (set.state === "approved" ? "承認済み" : "確認中")));
    row.append(name, button("確認", () => action(async () => {
      currentSet = set.id;
      renderSet(await ask("/sets/" + set.id));
    })));
    root.append(row);
  }
  if (sets.some(set => set.id === chosen)) picker.value = chosen;
  if (!sets.length) root.append(empty("素材棚の選択、またはバッチ処理の結果から素材セットを作成できます。"));
}

function renderSet(set) {
  const root = $("set-detail");
  const header = element("div", undefined, "section-head");
  const title = element("div");
  title.append(element("h2", set.name), element("p", "第 " + set.version + " 版 ・ " + (set.state === "approved" ? "承認済み" : "確認中")));
  header.append(title);
  const controls = element("div", undefined, "buttons");
  controls.append(button("タッチと強さを比較", () => openSweep(set)));
  if (set.state === "approved") {
    for (const [tail, label, name] of [["export", "画像とマニフェストを保存", set.name + ".zip"],
      ["manifest", "マニフェストを保存", "visual-set-manifest.json"]]) {
      const link = element("a", label, "button quiet");
      link.href = prefix + "/sets/" + set.id + "/" + tail + "?server=" + encodeURIComponent(server);
      link.download = name;
      controls.append(link);
    }
  }
  if (set.state !== "approved") {
    const draft = cutDrafts.get(set.id);
    const approve = button("この版を承認", () => action(async () => {
      if (!window.confirm("第 " + set.version + " 版を承認し、現在の判定を固定しますか？")) return;
      const body = { version: set.version, ...(draft?.version === set.version ? { cuts: draft.cuts } : {}) };
      const approved = await ask("/sets/" + set.id + "/approve", { body });
      cutDrafts.delete(set.id);
      renderSetList(await ask("/sets"));
      renderSet(approved);
      setMessage("素材セットを承認しました。");
    }), "primary");
    approve.disabled = set.items.some(i => i.availability === "unavailable" || i.decision === "pending")
      || !set.items.some(i => i.decision === "accepted");
    const cuts = button(draft?.version === set.version ? "カット順・役割を編集済み" : "カット順・役割を設定",
      () => openCuts(set, draft?.version === set.version ? draft.cuts : null));
    cuts.disabled = approve.disabled;
    controls.append(cuts, approve);
  }
  header.append(controls);
  const grid = element("div", undefined, "grid");
  for (const item of set.items) {
    const ref = item.asset;
    const local = ref.serverId === server;
    const name = item.name ?? ref.assetId.slice(0, 12);
    const card = element("article", undefined, "asset set-card");
    const controls = element("div", undefined, "asset-body");
    const available = item.availability === "available";
    if (available) card.append(preview(ref, name));
    controls.append(element("h3", name),
      element("small", available ? decisionLabels[item.decision] : "確認不可 ・ " + decisionLabels[item.decision], available ? "" : "error-text"));
    const buttons = element("div", undefined, "buttons");
    if (set.state !== "approved" && available) {
      for (const decision of ["accepted", "rejected", "pending"]) {
        if (decision === item.decision) continue;
        buttons.append(button(decisionLabels[decision], () => action(async () => {
          const updated = await ask("/sets/" + set.id + "/review", { body: { asset: ref, decision, version: set.version } });
          renderSet(updated);
        })));
      }
    }
    if (item.recipeId) buttons.append(button("レシピ", () => action(() => openRecipe(item.recipeId, local ? null : ref))));
    controls.append(buttons);
    card.append(controls);
    grid.append(card);
  }
  root.replaceChildren(header, grid);
}

function openCuts(set, saved) {
  const names = new Map(set.items.map(item => [item.asset.assetId, item.name]));
  const cuts = saved ?? set.items.filter(item => item.decision === "accepted").map(item => ({ asset: item.asset }));
  const edit = element("form");
  const rows = cuts.map((cut, index) => {
    const row = element("fieldset", undefined, "cut-row");
    row.append(element("legend", names.get(cut.asset.assetId)),
      inputField("number", "order", index + 1, "順番", { min: 1, max: cuts.length, required: true }),
      inputField("number", "seconds", cut.seconds ?? "", "表示秒数", { min: 0.01, max: 3600, step: "any" }),
      inputField("number", "lyricLine", cut.lyricLine ?? "", "歌詞行番号", { min: 1 }),
      inputField("text", "note", cut.note ?? "", "メモ", { maxLength: 1000 }));
    edit.append(row);
    return row.querySelectorAll("input");
  });
  const save = element("button", "カット順・役割を保存", "primary");
  save.type = "submit";
  edit.append(save);
  edit.addEventListener("submit", event => {
    event.preventDefault();
    action(() => {
      const positions = rows.map(([order]) => Number(order.value));
      if (new Set(positions).size !== cuts.length) throw new Error("各カットに異なる順番を指定してください。");
      const result = [];
      rows.forEach(([, seconds, lyricLine, note], index) => {
        result[positions[index] - 1] = { asset: cuts[index].asset, seconds: seconds.valueAsNumber || null,
          lyricLine: lyricLine.valueAsNumber || null, note: note.value.trim() || null };
      });
      cutDrafts.set(set.id, { version: set.version, cuts: result });
      $("work-dialog").close();
      renderSet(set);
    });
  });
  showWork("カット順と役割", edit);
}

function openSweep(set) {
  const edit = element("form");
  const list = element("div", undefined, "grid");
  const candidates = set.items.filter(item => item.availability === "available");
  const choices = candidates.map(item => {
    const check = element("input");
    check.type = "checkbox";
    const label = element("label", undefined, "asset sweep-choice");
    label.append(preview(item.asset, item.name), check, document.createTextNode(item.name));
    list.append(label);
    return check;
  });
  const submit = element("button", "比較ジョブを登録", "primary");
  submit.type = "submit";
  submit.disabled = !choices.length;
  edit.append(element("p", "代表画像を1〜2枚選んでください。イラスト・鉛筆画・細密画を強さ30・60・90%で比較します。"),
    list, selectField("hardware", [["auto", "自動"], ...Array.from(form.elements.hardware.options)
      .filter(option => option.value !== "auto").map(option => [option.value, option.text])], "計算資源"), submit);
  edit.addEventListener("submit", event => {
    event.preventDefault();
    action(async () => {
      const representatives = candidates.filter((_, index) => choices[index].checked).map(item => item.asset);
      if (!representatives.length || representatives.length > 2) throw new Error("代表画像を1〜2枚選んでください。");
      const job = await ask("/jobs/preview-sweep", { body: { setId: set.id, representatives, hardware: edit.elements.hardware.value } });
      $("work-dialog").close();
      await switchPanel("jobs");
      showEstimate(job.estimate);
      setMessage("比較ジョブを登録しました。");
    });
  });
  showWork("タッチと強さを比較", edit);
}

async function openSweepResults(job) {
  const content = element("div", undefined, "sweep-results");
  const set = await ask("/sets/" + job.sourceSetId);
  const results = await Promise.all(job.items.filter(item => item.status === "done")
    .map(async item => ({ item, recipe: await ask("/recipes/" + item.recipeId) })));
  for (const source of set.items.filter(source => results.some(result => result.recipe.input.assetId === source.asset.assetId &&
    result.recipe.input.serverId === source.asset.serverId))) {
    const grid = element("div", undefined, "grid");
    const original = element("article", undefined, "asset");
    original.append(preview(source.asset, source.name ?? "元画像"), element("h3", "元画像", "asset-body"));
    grid.append(original);
    for (const { item, recipe } of results.filter(result => result.recipe.input.assetId === source.asset.assetId &&
      result.recipe.input.serverId === source.asset.serverId)) {
      const name = touches.find(([key]) => key === recipe.settings.touch)?.[1] + " " + Math.round(recipe.settings.strength * 100) + "%";
      const card = element("article", undefined, "asset");
      const details = element("div", undefined, "asset-body");
      details.append(element("h3", name), button("このレシピでセット全体を一括登録", () => action(async () => {
        const batch = await ask("/recipes/replay", { body: { recipeId: recipe.id, setId: job.sourceSetId } });
        $("work-dialog").close();
        await refresh();
        showEstimate(batch.estimate);
        setMessage("選んだレシピでセット全体を登録しました。");
      })));
      card.append(preview(item.output, name), details);
      grid.append(card);
    }
    content.append(element("h3", source.name ?? "代表画像"), grid);
  }
  showWork("コンタクトシート", content);
}

async function openRecipe(id, remote = null) {
  const recipe = await ask(remote ? assetPath(remote) + "/recipe" : "/recipes/" + id);
  const detail = $("recipe-detail");
  const data = element("pre", JSON.stringify(recipe, null, 2));
  const title = element("p", recipe.engine.algorithm + " ・ " + hardwareLabels[recipe.engine.hardware]);
  if (remote) {
    detail.replaceChildren(title, data, element("p", "再実行するときは、この素材の保管先サーバーに切り替えてください。"));
    $("recipe-dialog").showModal();
    return;
  }
  const settings = recipe.settings;
  const variantSets = settings.operation === "resize" ? (await ask("/sets"))
    .filter(set => set.items.some(item => item.asset.serverId === recipe.input.serverId &&
      item.asset.assetId === recipe.input.assetId && item.asset.sha256 === recipe.input.sha256)) : [];
  const replay = async (change, setId) => {
    const body = { recipeId: id, ...(change ? { change } : {}), ...(setId ? { setId } : {}) };
    const job = await ask("/recipes/replay", { body });
    $("recipe-dialog").close();
    await switchPanel("jobs");
    showEstimate(job.estimate);
    setMessage("再実行を登録しました。");
  };
  const exact = button("このレシピで再実行", () => action(() => replay()), "primary");
  const values = { ...settings };
  if (settings.operation === "background" && matteEdgeRefinement) {
    values.maskRefineRadius ??= 0;
    values.maskErodePx ??= 0;
    values.decontaminateEdge ??= false;
  }
  if (settings.operation === "resize") {
    values.targetResolution = { width: settings.width, height: settings.height };
    values.fitMode ??= "crop";
  }
  const labels = { touch: "タッチ", strength: "強さ", targetResolution: "出力サイズ", fitMode: "収め方",
    padStyle: "余白の背景", padColor: "余白色", marginOffsets: "安全余白", color: "背景色",
    backgroundAsset: "背景画像の参照", referenceAsset: "基準画像の参照", colorMatchStrength: "補正の強さ",
    prompt: "背景の生成指示", seed: "シード", resolution: "モデル解像度", inspectionMode: "検品方法" };
  if (matteEdgeRefinement) {
    for (const [parameter, field] of Object.entries(matteFields)) labels[parameter] = field.label;
  }
  const fields = Object.entries(values).filter(([key, value]) => value !== null && key in labels);
  const edit = element("form");
  if (fields.length) {
    edit.append(selectField("parameter", fields.map(([key]) => [key, labels[key]]), "変更する項目"));
    if (variantSets.length) edit.append(selectField("variantSet", [["", "この素材のみ"],
      ...variantSets.map(set => [set.id, set.name])], "差分の対象"));
    const holder = element("div");
    edit.append(holder);
    function showField() {
      const parameter = edit.elements.parameter.value;
      const value = values[parameter];
      if (parameter === "touch") holder.replaceChildren(selectField("value", touches, "新しい値", value));
      else if (parameter === "fitMode") holder.replaceChildren(selectField("value", [["crop", "中央を切り抜く"], ["pad", "余白を足す"]], "新しい値", value));
      else if (parameter === "padStyle") holder.replaceChildren(selectField("value", padStyles, "新しい値", value));
      else if (parameter === "inspectionMode") holder.replaceChildren(selectField("value", cosmosInspection
        ? [["pixels", "画素検査"], ["cosmos", "画素検査と Cosmos タグ"]] : [["pixels", "画素検査"]], "新しい値", value));
      else if (parameter in matteFields) holder.replaceChildren(matteField(parameter, "value", value));
      else if (parameter === "targetResolution") {
        const size = value.width + "," + value.height;
        holder.replaceChildren(selectField("value", [...resizePresets, ...resizePresets.some(([preset]) => preset === size)
          ? [] : [[size, value.width + " × " + value.height]], ["custom", "カスタム"]], "新しいサイズ", size));
        const dimensions = element("div", undefined, "pair");
        dimensions.append(inputField("number", "width", value.width, "幅（px）", { min: 1, max: 8192, required: true }),
          inputField("number", "height", value.height, "高さ（px）", { min: 1, max: 8192, required: true }));
        dimensions.hidden = true;
        holder.append(dimensions);
        edit.elements.value.addEventListener("change", () => { dimensions.hidden = edit.elements.value.value !== "custom"; });
      } else if (parameter === "marginOffsets") {
        holder.replaceChildren(...marginSides.map(([name, label]) => inputField("number", name, value[name] * 100,
          label + "（%）", { min: 0, max: 99, step: "0.1", required: true })));
      }
      else holder.replaceChildren(inputField(typeof value === "number" ? "number" : "text", "value",
        typeof value === "object" ? JSON.stringify(value) : value, "新しい値", { required: true }));
      if (typeof value === "number" && !(parameter in matteFields)) edit.elements.value.step = "any";
    }
    edit.elements.parameter.addEventListener("change", showField);
    showField();
    const submit = element("button", "1項目だけ変えて再実行", "quiet");
    submit.type = "submit";
    edit.append(submit);
    edit.addEventListener("submit", event => {
      event.preventDefault();
      action(() => {
        const parameter = edit.elements.parameter.value;
        let value = edit.elements.value?.value;
        if (parameter === "targetResolution") {
          const [width, height] = value === "custom" ? [edit.elements.width.valueAsNumber, edit.elements.height.valueAsNumber]
            : value.split(",").map(Number);
          value = { width, height };
        } else if (parameter === "marginOffsets")
          value = Object.fromEntries(marginSides.map(([name]) => [name, edit.elements[name].valueAsNumber / 100]));
        else if (typeof values[parameter] === "number") value = Number(value);
        else if (typeof values[parameter] === "boolean") value = value === "true";
        else if (typeof values[parameter] === "object") value = JSON.parse(value);
        return replay({ parameter, value }, edit.elements.variantSet?.value);
      });
    });
  }
  detail.replaceChildren(title, data, exact, edit);
  $("recipe-dialog").showModal();
}

async function connections() {
  const data = await ask("/atelier/servers", { api: false });
  const picker = $("server");
  picker.replaceChildren();
  if (!data.servers.length) picker.add(new Option("未接続", ""));
  for (const entry of data.servers) picker.add(new Option(entry.name, entry.id));
  picker.value = data.selected ?? "";
  const active = data.servers.find(entry => entry.id === data.selected);
  $("connection-status").textContent = connectionLabels[active?.state] ?? "素材サーバー";
  if (data.selected !== server) {
    server = data.selected;
    generation++;
    currentSet = null;
    selected.clear();
    cutDrafts.clear();
    $("set-detail").replaceChildren();
    $("job-list").replaceChildren();
    renderAssets([]);
    updateSelection();
  }
}

async function refresh(all = false) {
  if (!server) {
    renderAssets([]);
    $("karaoke-jobs").replaceChildren();
    return;
  }
  const key = generation;
  if (panel === "karaoke") {
    const jobs = await ask("/karaoke/jobs");
    if (key === generation && panel === "karaoke")
      showKaraokeJobs($("karaoke-jobs"), jobs, { ask, refresh, action, button, empty, element, prefix, server });
  }
  if (all || panel === "assets") {
    const [assets, shown] = await Promise.all([ask("/assets"), assetQuery
      ? ask("/assets/search", { body: { query: assetQuery } }) : Promise.resolve(undefined)]);
    if (key !== generation) return;
    renderAssets(assets, shown);
  }
  if (all || panel === "jobs") {
    const jobs = await ask("/jobs");
    if (key !== generation) return;
    renderJobs(jobs);
  }
  if (all || panel === "sets") {
    const sets = await ask("/sets");
    if (key !== generation) return;
    renderSetList(sets);
    if (panel === "sets" && currentSet) {
      const set = await ask("/sets/" + currentSet);
      if (key === generation) renderSet(set);
    }
  }
  if ((all || panel === "sets" || panel === "assets") && form.elements.referenceAsset) await updateReferences();
  if (all) {
    const nodes = await ask("/nodes");
    if (key !== generation) return;
    const available = nodes.some(node => node.cosmosInspection);
    if (cosmosInspection !== available) {
      cosmosInspection = available;
      if (form.elements.operation.value === "inspect") renderAdjustments();
    }
    const canRefineMatte = nodes.some(node => node.serverId === server && node.matteEdgeRefinement);
    if (matteEdgeRefinement !== canRefineMatte) {
      matteEdgeRefinement = canRefineMatte;
      if (form.elements.operation.value === "background") renderAdjustments();
    }
    const picker = form.elements.hardware;
    const previous = picker.value;
    picker.options.length = 2;
    for (const type of ["gpu", "cloud-gpu"])
      if (nodes.some(node => node.hardware === type)) picker.add(new Option(hardwareLabels[type], type));
    picker.value = [...picker.options].some(option => option.value === previous) ? previous : "auto";
    updateModelOptions();
  }
}

async function switchPanel(name) {
  panel = name;
  document.querySelector(".workspace").classList.toggle("karaoke-active", name === "karaoke");
  document.querySelector(".editor").hidden = name === "karaoke";
  for (const tab of document.querySelectorAll("[data-panel]")) {
    const active = tab.dataset.panel === name;
    tab.classList.toggle("active", active);
    if (active) tab.setAttribute("aria-current", "page");
    else tab.removeAttribute("aria-current");
    $(tab.dataset.panel).hidden = !active;
  }
  await refresh();
}

async function upload(files) {
  const images = Array.from(files).filter(file => /\.(png|jpe?g)$/i.test(file.name));
  if (!images.length) throw new Error("PNG または JPEG を選択してください。");
  if (images.length > 200) throw new Error("一度に取り込める画像は200件までです。");
  const pinned = server;
  const size = 384 * 1024;
  const failed = [];
  let imported = 0;
  for (const file of images) {
    setMessage("取り込み中 " + (imported + failed.length + 1) + " / " + images.length + " 件");
    try {
      if (file.size > 16 * 1024 * 1024) throw new Error("16 MiB を超えています。");
      const bytes = new Uint8Array(await file.arrayBuffer());
      const hash = new Uint8Array(await crypto.subtle.digest("SHA-256", bytes));
      const sha256 = Array.from(hash, byte => byte.toString(16).padStart(2, "0")).join("");
      const chunks = [];
      for (let offset = 0; offset < bytes.length; offset += size) {
        const part = bytes.subarray(offset, offset + size);
        let binary = "";
        for (let start = 0; start < part.length; start += 8192)
          binary += String.fromCharCode(...part.subarray(start, start + 8192));
        const result = await ask("/assets/chunks", { body: { bytes: btoa(binary) }, pinned });
        chunks.push(result.sha256);
      }
      const asset = await ask("/assets/import", { body: { name: file.name, sha256, chunks }, pinned });
      if (server === pinned) selected.set(asset.reference.assetId, asset.reference);
      imported++;
    } catch (error) { failed.push(file.name + "（" + error.message + "）"); }
  }
  await switchPanel("assets");
  updateSelection();
  setMessage(imported + " 件を取り込みました。" + (failed.length ? " 失敗 " + failed.join("、") : ""), failed.length > 0);
}

for (const tab of document.querySelectorAll("[data-panel]"))
  tab.addEventListener("click", () => action(() => switchPanel(tab.dataset.panel)));
for (const name of ["files", "folder"]) $(name).addEventListener("change", event => {
  const files = Array.from(event.target.files);
  event.target.value = "";
  if (files.length) action(() => upload(files));
});
$("connection-file").addEventListener("change", event => {
  const file = event.target.files[0];
  event.target.value = "";
  if (file) action(async () => {
    if (file.size > 16384) throw new Error("接続ファイルのサイズを確認してください。");
    await ask("/atelier/servers", { api: false, body: JSON.parse(await file.text()) });
    await connections();
    await refresh(true);
  });
});
$("server").addEventListener("change", event => action(async () => {
  await ask("/atelier/servers/" + event.target.value, { api: false, method: "POST" });
  await connections();
  await refresh(true);
}));
$("create-set").addEventListener("click", () => action(async () => {
  const name = window.prompt("素材セットの名前");
  if (!name) return;
  const set = await ask("/sets", { body: { name, assets: [...selected.values()] } });
  currentSet = set.id;
  await switchPanel("sets");
}));
$("refresh-jobs").addEventListener("click", () => action(() => refresh()));
$("asset-search").addEventListener("submit", event => {
  event.preventDefault();
  assetQuery = event.target.elements.query.value.trim();
  action(() => refresh());
});
$("estimate-button").addEventListener("click", () => action(async () => showEstimate(await ask("/jobs/estimate", { body: batch() }))));
form.elements.operation.addEventListener("change", renderAdjustments);
form.elements.hardware.addEventListener("change", updateModelOptions);
form.elements.set.addEventListener("change", () => action(updateReferences));
form.addEventListener("change", () => { $("estimate").hidden = true; });
form.addEventListener("submit", event => {
  event.preventDefault();
  action(async () => {
    const job = await ask("/jobs", { body: batch() });
    await switchPanel("jobs");
    showEstimate(job.estimate);
    setMessage("一括編集を登録しました。");
  });
});
$("recipe-dialog").querySelector(".close").addEventListener("click", () => $("recipe-dialog").close());
$("work-dialog").querySelector(".close").addEventListener("click", () => $("work-dialog").close());
renderAdjustments();
updateSelection();
bindKaraoke($("karaoke-form"), { ask, action, setMessage, refresh });
await action(async () => { await connections(); await refresh(true); });
setInterval(async () => {
  if (busy || loading || $("recipe-dialog").open || $("work-dialog").open) return;
  loading = true;
  try {
    const previous = server;
    await connections();
    if (previous !== server) await refresh(true);
    else if (panel === "jobs" || panel === "karaoke") await refresh();
  } catch (error) { setMessage(error.message, true); }
  finally { loading = false; }
}, 5000);
