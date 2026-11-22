// Status > Downloads: import files from the user's PC for posts the site deleted. File names are matched to posts by id. The ids
// are checked first, so only wanted files are uploaded (not the ones this device has, or that the site still serves); each wanted
// file is then sent on its own (raw body, file name in X-File-Name) and the server says whether its MD5 is the original's.
const NAME = /^(?:\[\d+\]\s*)?(\d{4,})((?:\.[a-z0-9]+)?)\.(jpe?g|png|gif|webp|webm|mp4|mkv|swf)$/i;
const PARALLEL = 3;
const CHECK_CHUNK = 200;

interface Candidate { file: File; id: number; raw: boolean; }
interface Verdict { id: number; verdict: string; message: string; }

// Shown in the summary; AlreadyLocal is only counted, the others are listed too.
const LABELS: Record<string, string> = {
  AlreadyLocal: "already here",
  StillOnline: "still on the site",
  Binned: "in the bin",
  Blacklisted: "blacklisted",
  Inferior: "inferior versions",
  LowEffortEdit: "low-effort edits",
  ReasonUnknown: "reason not known yet",
  NotInCatalog: "not in the catalog",
  NoId: "no post id in the name",
};

const root = document.getElementById("importer");
if (root) {
  const checkUrl = root.dataset.checkUrl!;
  const uploadUrl = root.dataset.uploadUrl!;
  const drop = root.querySelector<HTMLElement>(".file-import-drop")!;
  const summary = root.querySelector<HTMLElement>(".file-import-summary")!;
  const log = root.querySelector<HTMLElement>(".file-import-log")!;
  let counts: Record<string, number> = {};
  let status = "";
  let busy = false;

  const count = (key: string) => { counts[key] = (counts[key] ?? 0) + 1; };

  const line = (text: string, kind: string) => {
    const li = document.createElement("li");
    li.className = kind;
    li.textContent = text;
    log.append(li);
    return li;
  };

  const showSummary = (finished: boolean) => {
    summary.hidden = false;
    const parts = [
      counts.original ? `${counts.original} original` : "",
      counts.custom ? `${counts.custom} custom` : "",
      ...Object.keys(LABELS).filter((key) => counts[key]).map((key) => `${counts[key]} ${LABELS[key]}`),
      counts.failed ? `${counts.failed} failed` : "",
    ].filter(Boolean);
    summary.textContent = (parts.join(", ") || "Nothing to do") + (finished ? "." : "... ") + (finished ? "" : status);
    if (finished && (counts.original || counts.custom)) {
      const reload = document.createElement("a");
      reload.href = location.href;
      reload.textContent = " Reload the page";
      summary.append(reload);
    }
  };

  async function readEntry(entry: FileSystemEntry, out: File[]): Promise<void> {
    if (entry.isFile) {
      out.push(await new Promise<File>((resolve, reject) => (entry as FileSystemFileEntry).file(resolve, reject)));
    } else if (entry.isDirectory) {
      const reader = (entry as FileSystemDirectoryEntry).createReader();
      for (;;) {
        const batch = await new Promise<FileSystemEntry[]>((resolve, reject) => reader.readEntries(resolve, reject));
        if (batch.length === 0) break;
        for (const child of batch) await readEntry(child, out);
      }
    }
  }

  async function check(ids: number[]): Promise<Map<number, Verdict>> {
    const verdicts = new Map<number, Verdict>();
    for (let i = 0; i < ids.length; i += CHECK_CHUNK) {
      status = `checking ${Math.min(i + CHECK_CHUNK, ids.length)} of ${ids.length} ids`;
      showSummary(false);
      const response = await fetch(checkUrl, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ ids: ids.slice(i, i + CHECK_CHUNK) }) });
      if (!response.ok) throw new Error(`the check failed (HTTP ${response.status})`);
      for (const verdict of (await response.json()).results as Verdict[]) verdicts.set(verdict.id, verdict);
    }
    return verdicts;
  }

  async function upload(candidate: Candidate) {
    const row = line(`${candidate.file.name}: uploading...`, "pending");
    try {
      const response = await fetch(`${uploadUrl}/${candidate.id}`, { method: "POST", body: candidate.file, headers: { "X-File-Name": encodeURIComponent(candidate.file.name) } });
      const body = await response.json().catch(() => ({}));
      if (response.ok) {
        const original = body.original === true;
        count(original ? "original" : "custom");
        row.className = "ok";
        row.textContent = `${candidate.file.name}: ${original ? "original (MD5 matches)" : "custom (MD5 differs)"}, #${candidate.id}`;
      } else if (response.status === 409) {
        count(body.verdict in LABELS ? body.verdict : "failed");
        row.className = "skipped";
        row.textContent = `${candidate.file.name}: skipped, ${body.error} (#${candidate.id})`;
      } else {
        count("failed");
        row.className = "failed";
        row.textContent = `${candidate.file.name}: ${body.error ?? "failed (" + response.status + ")"}`;
      }
    } catch (error) {
      count("failed");
      row.className = "failed";
      row.textContent = `${candidate.file.name}: ${(error as Error).message}`;
    }
    showSummary(false);
  }

  async function handle(files: File[]) {
    if (busy) return;
    busy = true;
    counts = {};
    status = "";
    log.replaceChildren();
    try {
      // Names are matched by id. When an id has a ".raw" file, the others for it are left out: those were post-processed.
      const named: Candidate[] = [];
      for (const file of files) {
        const match = NAME.exec(file.name);
        if (!match) { count("NoId"); continue; }
        named.push({ file, id: Number(match[1]), raw: match[2].toLowerCase() === ".raw" });
      }
      const rawIds = new Set(named.filter((c) => c.raw).map((c) => c.id));
      const candidates: Candidate[] = [];
      const seen = new Set<string>();
      for (const c of named) {
        if (!c.raw && rawIds.has(c.id)) continue; // the .raw file of this id is used
        const key = `${c.id}:${c.raw}`;
        if (seen.has(key)) { line(`${c.file.name}: skipped, another file for #${c.id} was dropped too`, "skipped"); continue; }
        seen.add(key);
        candidates.push(c);
      }

      const verdicts = await check([...new Set(candidates.map((c) => c.id))]);
      const queue: Candidate[] = [];
      for (const c of candidates) {
        const verdict = verdicts.get(c.id);
        if (verdict?.verdict === "Wanted") { queue.push(c); continue; }
        const key = verdict?.verdict ?? "NotInCatalog";
        count(key);
        if (key !== "AlreadyLocal") line(`${c.file.name}: skipped, ${verdict?.message ?? "not in the catalog"} (#${c.id})`, "skipped");
      }

      showSummary(false);
      const total = queue.length;
      const next = async () => {
        for (let c = queue.shift(); c; c = queue.shift()) {
          status = `uploading, ${queue.length} of ${total} left`;
          await upload(c);
        }
      };
      await Promise.all(Array.from({ length: Math.min(PARALLEL, queue.length) }, next));
    } catch (error) {
      count("failed");
      line((error as Error).message, "failed");
    }
    showSummary(true);
    busy = false;
  }

  for (const input of root.querySelectorAll<HTMLInputElement>("input[type=file]"))
    input.addEventListener("change", () => { void handle(Array.from(input.files ?? [])); input.value = ""; });

  drop.addEventListener("dragover", (event) => { event.preventDefault(); drop.classList.add("dragging"); });
  drop.addEventListener("dragleave", () => drop.classList.remove("dragging"));
  drop.addEventListener("drop", async (event) => {
    event.preventDefault();
    drop.classList.remove("dragging");
    const files: File[] = [];
    // The entries must be taken before the first await: the list is emptied afterwards.
    const entries = Array.from(event.dataTransfer?.items ?? []).map((item) => item.webkitGetAsEntry?.()).filter((e): e is FileSystemEntry => !!e);
    if (entries.length > 0) for (const entry of entries) await readEntry(entry, files);
    else files.push(...Array.from(event.dataTransfer?.files ?? []));
    void handle(files);
  });
}
