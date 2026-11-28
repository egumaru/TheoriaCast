function readSrt(text) {
  const clock = "(\\d{2,}:\\d{2}:\\d{2}[,.]\\d{3})";
  const timing = new RegExp("^" + clock + "\\s+-->\\s+" + clock + "(?:\\s+.*)?$");
  const seconds = value => {
    const [h, m, s] = value.replace(",", ".").split(":").map(Number);
    return h * 3600 + m * 60 + s;
  };
  return text.replace(/^\uFEFF/, "").replace(/\r\n?/g, "\n").trim().split(/\n\s*\n/).map(cue => {
    const lines = cue.split("\n");
    if (/^\d+$/.test(lines[0].trim())) lines.shift();
    const match = lines.shift()?.trim().match(timing);
    if (!match || !lines.length) throw new Error("SRT の時刻と歌詞を確認してください。");
    return { start: seconds(match[1]), end: seconds(match[2]), text: lines.join("\n") };
  });
}

async function stageWav(file, ask) {
  if (file.size <= 0 || file.size > 128 * 1024 * 1024) throw new Error("WAV は128 MiB以内です。");
  const bytes = new Uint8Array(await file.arrayBuffer());
  const digest = new Uint8Array(await crypto.subtle.digest("SHA-256", bytes));
  const sha256 = Array.from(digest, byte => byte.toString(16).padStart(2, "0")).join("");
  const chunks = [];
  const size = 384 * 1024;
  for (let offset = 0; offset < bytes.length; offset += size) {
    const piece = bytes.subarray(offset, offset + size);
    let binary = "";
    for (let start = 0; start < piece.length; start += 8192)
      binary += String.fromCharCode(...piece.subarray(start, start + 8192));
    const result = await ask("/karaoke/chunks", { body: { bytes: btoa(binary) } });
    chunks.push(result.sha256);
  }
  return { sha256, chunks };
}

export function bindKaraoke(form, { ask, action, setMessage, refresh }) {
  const fields = form.elements;
  fields.mode.addEventListener("change", () => { fields.guide.required = fields.mode.value !== "lyrics"; });
  form.addEventListener("submit", event => {
    event.preventDefault();
    action(async () => {
      const mode = fields.mode.value;
      const noteFile = fields.guide.files[0];
      const cueFile = fields.cues.files[0];
      const guideValue = noteFile ? JSON.parse(await noteFile.text()) : [];
      const guide = guideValue.recipe ?? guideValue;
      const notes = Array.isArray(guide) ? guide : guide.notes;
      if (mode !== "lyrics" && !Array.isArray(notes)) throw new Error("ガイド譜 JSON の notes を確認してください。");
      const raw = await cueFile.text();
      const cueValue = /\.srt$/i.test(cueFile.name) ? readSrt(raw) : JSON.parse(raw);
      const lyrics = Array.isArray(cueValue) ? cueValue : cueValue.cues ?? cueValue.provenance?.cues;
      if (!Array.isArray(lyrics)) throw new Error("歌詞 JSON の cues を確認してください。");
      const more = fields.style.value.trim() ? JSON.parse(fields.style.value) : {};
      if (!more || typeof more !== "object" || Array.isArray(more)) throw new Error("詳細設定は JSON オブジェクトです。");
      const style = { width: fields.width.valueAsNumber, height: fields.height.valueAsNumber,
        fps: fields.fps.valueAsNumber, scroll: fields.scroll.valueAsNumber,
        ...(fields.lyrics.value ? { lyrics: fields.lyrics.value } : {}), ...more };
      setMessage("音源を転送中です。");
      const audio = await stageWav(fields.audio.files[0], ask);
      await ask("/karaoke/jobs", { body: { mode, style, input: { title: fields.title.value, audio, lyrics, notes,
        trackId: guide.source_track ?? cueValue.id ?? null, bpm: guide.bpm ?? null } } });
      setMessage("カラオケ動画の生成を登録しました。");
      await refresh();
    });
  });
}

export function showKaraokeJobs(list, jobs, { ask, refresh, action, button, empty, element, prefix, server }) {
  list.replaceChildren();
  if (!jobs.length) { list.append(empty("生成したカラオケ動画がここに表示されます。")); return; }
  const modes = { bars: "カラオケ風バー", score: "流れる楽譜", lyrics: "歌詞オーバーレイ" };
  const states = { queued: "待機中", running: "生成中", done: "完了", failed: "失敗" };
  for (const job of jobs) {
    const row = element("article", undefined, "set-row");
    const title = element("div");
    title.append(element("h3", job.title), element("small", (modes[job.mode] ?? "カラオケ動画") + " ・ " + states[job.state]));
    if (job.error) title.append(element("p", job.error, "error-text"));
    const links = element("div", undefined, "buttons");
    const route = "/karaoke/jobs/" + job.id;
    const downloads = [["レシピJSON", "/recipe", "karaoke-recipe.json"]];
    if (job.state === "done") downloads.push(["動画を保存", "/video", job.id + (job.mode === "lyrics" ? ".mov" : ".mp4")]);
    for (const [label, tail, download] of downloads) {
      const link = element("a", label, "button quiet");
      link.href = prefix + route + tail + "?server=" + encodeURIComponent(server);
      link.download = download;
      links.append(link);
    }
    if (job.state === "done") links.append(button("再生成", () => action(async () => {
      await ask(route + "/replay", { body: {} });
      await refresh();
    })));
    row.append(title, links);
    list.append(row);
  }
}
