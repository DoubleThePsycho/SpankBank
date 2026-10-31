// Live progress on the Status page: polls /status/progress (relayed from the worker) and draws it Windows-Update style.
interface Phase { name: string; state: "pending" | "running" | "done" | "skipped" | "failed" }
interface Step { detail: string; done: number; total: number | null; unit: string; perSecond: number | null; etaSeconds: number | null }
interface Run { title: string; phases: Phase[]; step: Step | null; result: string | null; startedAt: string; finishedAt: string | null }
interface ActiveFile { postId: number; name: string; done: number; total: number | null }
interface RecentFile { postId: number; md5: string; ext: string; status: string }
interface Downloads {
  active: boolean; total: number; done: number; failed: number; filesPerSecond: number | null; etaSeconds: number | null;
  current: ActiveFile[]; recent: RecentFile[]; idleReason: string | null;
}
interface Progress { online?: false; run?: Run | null; downloads?: Downloads }

const RUN_RESULT_VISIBLE_MS = 5 * 60 * 1000;
const section = document.getElementById("activity");

const number = new Intl.NumberFormat("en-US");

function bytes (n: number): string {
  if (n < 1024) return `${n} B`;
  const units = ["KB", "MB", "GB", "TB"];
  let value = n / 1024, unit = 0;
  while (value >= 1024 && unit < units.length - 1) { value /= 1024; unit++; }
  return `${value.toFixed(value < 10 ? 2 : 1)} ${units[unit]}`;
}

function duration (seconds: number): string {
  if (seconds < 90) return "less than a minute";
  const minutes = Math.round(seconds / 60);
  if (minutes < 90) return `about ${minutes} minutes`;
  const hours = seconds / 3600;
  if (hours < 36) return `about ${hours.toFixed(1)} hours`;
  const days = Math.round(hours / 24);
  return days > 60 ? "more than two months" : `about ${days} days`;
}

function amount (value: number, unit: string): string {
  return unit === "bytes" ? bytes(value) : number.format(Math.round(value));
}

function rate (perSecond: number, unit: string): string {
  return unit === "bytes" ? `${bytes(perSecond)}/s` : `${number.format(Math.round(perSecond))} ${unit}/s`;
}

function escapeHtml (text: string): string {
  return text.replace(/[&<>"]/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", "\"": "&quot;" })[c]!);
}

/** A progress bar; without a known total (or when the estimate was exceeded) it is shown as busy. */
function bar (done: number, total: number | null, small = false): string {
  const known = total !== null && total > 0 && done <= total;
  const percent = known ? Math.min(100, (done / total!) * 100) : 0;
  return `<div class="status-bar${small ? " small" : ""}${known ? "" : " busy"}"><div class="fill" style="width:${known ? percent.toFixed(1) : 100}%"></div></div>`;
}

const PHASE_MARK: Record<Phase["state"], string> = { pending: "○", running: "●", done: "✓", skipped: "–", failed: "✗" };

function renderRun (run: Run): string {
  const finished = run.finishedAt !== null;
  const phases = run.phases.map((p) => `<li class="${p.state}"><span class="mark">${PHASE_MARK[p.state]}</span> ${escapeHtml(p.name)}</li>`).join("");

  let step = "";
  if (!finished && run.step) {
    const s = run.step;
    const known = s.total !== null && s.total > 0 && s.done <= s.total;
    const parts = [`${amount(s.done, s.unit)}${s.total ? ` of ${amount(s.total, s.unit)}` : ""}`];
    if (s.perSecond) parts.push(rate(s.perSecond, s.unit));
    if (s.etaSeconds) parts.push(`${duration(s.etaSeconds)} left`);
    step = `<div class="status-step">
      <div class="status-step-title">${escapeHtml(s.detail)}${known ? `<span class="percent">${Math.floor((s.done / s.total!) * 100)}%</span>` : ""}</div>
      ${bar(s.done, s.total)}
      <div class="status-step-text">${parts.join(" · ")}</div>
    </div>`;
  }

  const result = finished && run.result ? `<p class="status-result">${escapeHtml(run.result)}</p>` : "";
  return `<h2>${escapeHtml(finished ? run.title.replace(/^Updating/, "Updated") : run.title + "…")}</h2>
    ${finished ? "" : "<p class=\"status-hint\">You can leave this page; the work continues in the background.</p>"}
    <ol class="status-phases">${phases}</ol>${step}${result}`;
}

function renderDownloads (d: Downloads): string {
  if (!d.active) {
    return d.idleReason ? `<h2>Downloading posts</h2><p class="status-idle">${escapeHtml(d.idleReason)}</p>` : "";
  }

  const finished = d.done + d.failed;
  const percent = d.total > 0 ? Math.min(100, Math.floor((finished / d.total) * 100)) : 0;
  const parts = [`${number.format(finished)} of ${number.format(d.total)} posts`];
  if (d.filesPerSecond) parts.push(`${d.filesPerSecond.toFixed(1)} files/s`);
  if (d.etaSeconds) parts.push(`${duration(d.etaSeconds)} left`);
  if (d.failed > 0) parts.push(`${number.format(d.failed)} could not be saved`);

  const current = d.current.map((f) => `<li><span class="name">${escapeHtml(f.name)}</span>
      ${f.total ? bar(f.done, f.total, true) : bar(0, null, true)}
      <span class="size">${f.total ? `${bytes(f.done)} of ${bytes(f.total)}` : f.done ? bytes(f.done) : "waiting"}</span></li>`).join("");

  const recent = d.recent.map((f) => {
    const src = `/media/preview/${f.md5.slice(0, 2)}/${f.md5.slice(2, 4)}/${f.md5}.jpg`;
    return `<a href="/posts/${f.postId}" title="Post #${f.postId} (${f.status})"><img src="${src}" alt="" loading="lazy" onerror="this.style.visibility='hidden'"></a>`;
  }).join("");

  return `<h2>Downloading posts…</h2>
    <div class="status-big"><span class="percent">${percent}%</span></div>
    ${bar(finished, d.total)}
    <div class="status-step-text">${parts.join(" · ")}</div>
    ${current ? `<ul class="status-files">${current}</ul>` : ""}
    ${recent ? `<div class="status-recent">${recent}</div>` : ""}`;
}

function show (element: Element | null, html: string | null) {
  if (!element) return;
  const target = element as HTMLElement;
  target.hidden = html === null || html === "";
  if (!target.hidden && target.innerHTML !== html) target.innerHTML = html!;
}

async function refresh () {
  if (!section) return;
  let progress: Progress;
  try {
    progress = await (await fetch("/status/progress", { cache: "no-store" })).json();
  } catch {
    progress = { online: false };
  }

  const offline = section.querySelector<HTMLElement>(".status-offline")!;
  offline.hidden = progress.online !== false;

  let runHtml: string | null = null;
  const run = progress.run;
  if (run) {
    const recentlyFinished = run.finishedAt !== null && Date.now() - Date.parse(run.finishedAt) < RUN_RESULT_VISIBLE_MS;
    if (run.finishedAt === null || recentlyFinished) runHtml = renderRun(run);
  }
  show(section.querySelector(".status-run"), runHtml);
  show(section.querySelector(".status-downloads"), progress.downloads ? renderDownloads(progress.downloads) : null);
}

if (section) {
  const tick = async () => {
    if (!document.hidden) await refresh();
    window.setTimeout(tick, 1000);
  };
  void tick();
}
