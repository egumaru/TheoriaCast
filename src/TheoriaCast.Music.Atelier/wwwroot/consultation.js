"use strict";
(() => {
  const form = document.querySelector("#composeForm");
  const help = document.querySelector("#studioHelp");
  const question = document.querySelector("#questionForm");
  const status = document.querySelector("#studioStatus");
  const preview = document.querySelector("#studioPreview");
  const fields = {kind:window.musicText("曲の種類"), theme:window.musicText("テーマ"), lyrics:window.musicText("歌いたい言葉"), style:window.musicText("雰囲気"),
    instruments:window.musicText("楽器・演奏の希望"), seconds:window.musicText("長さ（秒）"), bpm:window.musicText("曲の速さ"), shorts:window.musicText("3分以内に収める"), alternate:window.musicText("もう一台で作る"),prompt_md:window.musicText("曲の指示書")};
  const storageKey = "theoria.atelier." + (window.atelierServerId || location.origin);
  let current = null;
  let requestId = null;
  let waiting = false;

  function draft() {
    return Object.fromEntries(Object.keys(fields).map(key => {
      const input = form.elements.namedItem(key);
      if (input.type === "checkbox") return [key, input.checked];
      if (input.type === "number") return [key, input.value === "" ? null : Number(input.value)];
      return [key, input.value];
    }));
  }
  function setField(key, value) {
    const input = form.elements.namedItem(key);
    if (input.type === "checkbox") input.checked = value;
    else {
      if (key === "style" && value && !Array.from(input.options).some(o => o.value === value))
        input.add(new Option(value, value));
      input.value = value ?? "";
    }
  }
  function remember() {
    sessionStorage.setItem(storageKey, JSON.stringify({id:requestId, draft:draft(), question:question.elements.question.value}));
  }
  function describe(key, value) {
    if (key === "kind") return value === "generate_vocal" ? window.musicText("歌のある曲") : window.musicText("楽器だけ");
    if (typeof value === "boolean") return value ? window.musicText("使う") : window.musicText("使わない");
    if (key === "style") return [...form.elements.style.options].find(o => o.value === value)?.text || value;
    return value == null || value === "" ? window.musicText("未入力") : String(value);
  }
  function control(label, fn) {
    const button = el("button", label);
    button.type = "button";
    button.onclick = async () => {
      button.disabled = true;
      try { await fn(); } catch (e) { status.textContent = e.message; } finally { button.disabled = false; }
    };
    preview.append(button);
  }
  async function decide(decision) {
    return await api("/studio/requests/" + current.id + "/decision", "POST", {decision});
  }
  function show(record) {
    current = record;
    preview.replaceChildren();
    if (record.status === "waiting") {
      status.textContent = window.musicText("Codex に渡す依頼文を、いつもの Codex の会話に貼り付けてください。返事はここに届きます。");
      const task = window.musicText("TheoriaCast.Music の作曲相談 ") + record.id + window.musicText(" を get_studio_request で読み、私への提案を suggest_in_studio で画面に返してください。");
      const label = el("label", window.musicText("Codex に渡す依頼文"));
      const copy = el("textarea");
      copy.readOnly = true;
      copy.value = task;
      label.append(copy);
      preview.append(label);
      control(window.musicText("依頼文をコピー"), async () => {
        copy.select();
        if (navigator.clipboard?.writeText) await navigator.clipboard.writeText(task);
        else if (!document.execCommand("copy")) { status.textContent = window.musicText("選択した依頼文をコピーしてください。"); return; }
        status.textContent = window.musicText("依頼文をコピーしました。Codex の会話に貼り付けてください。");
      });
    } else if (record.status === "suggested") {
      status.textContent = window.musicText("Codex から提案が届きました。");
      preview.append(el("p", record.reply));
      const changes = Object.entries(record.changes || {}).filter(([key, value]) => key in fields && value != null);
      for (const [key, value] of changes) {
        const row = el("details");
        row.open = true;
        row.append(el("summary", fields[key]), el("p", window.musicText("相談時　") + describe(key, record.draft[key])),
          el("p", window.musicText("提案　") + describe(key, value)));
        preview.append(row);
      }
      control(changes.length ? window.musicText("この提案を下書きに使う") : window.musicText("確認した"), async () => {
        const values = draft();
        const conflicts = changes.filter(([k,v]) => values[k] !== record.draft[k] && values[k] !== v);
        if (conflicts.length) {
          status.textContent = conflicts.map(([k]) => fields[k]).join(window.musicText("、")) + window.musicText("は相談の後で編集されました。今の下書きで改めて相談できます。");
          return;
        }
        form.inert = question.inert = true;
        try {
          const result = await decide("applied");
          for (const [k,v] of changes) setField(k,v);
          kindChanged();
          remember();
          show(result);
        } finally { form.inert = question.inert = false; }
      });
    } else {
      status.textContent = record.status === "applied" ? window.musicText("提案を確認しました。下書きはこのまま編集・予約できます。") : window.musicText("相談を閉じました。");
      return;
    }
    control(window.musicText("相談を閉じる"), async () => show(await decide("closed")));
  }
  async function poll() {
    if (!requestId || waiting || (current && current.status !== "waiting")) return;
    const id = requestId;
    try {
      const record = await api("/studio/requests/" + id);
      if (requestId === id && current?.status !== record.status) show(record);
    } catch (e) { if (requestId === id) status.textContent = e.message; }
  }
  question.onsubmit = async e => {
    e.preventDefault();
    if (waiting) return;
    question.inert = waiting = true;
    try {
      const result = await api("/studio/requests", "POST", {question:question.elements.question.value, draft:draft(), replaces:requestId});
      requestId = result.id;
      remember();
      show(result);
    } catch (e) { status.textContent = e.message; } finally { question.inert = waiting = false; }
  };
  try {
    const saved = JSON.parse(sessionStorage.getItem(storageKey));
    if (saved) {
      for (const [k,v] of Object.entries(saved.draft)) if (k in fields) setField(k,v);
      question.elements.question.value = saved.question || "";
      requestId = saved.id;
      help.open = Boolean(requestId);
      kindChanged();
    }
  } catch { sessionStorage.removeItem(storageKey); }
  form.addEventListener("input", remember);
  question.addEventListener("input", remember);
  poll();
  window.setInterval(poll, 1500);
})();
