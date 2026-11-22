// The "missing posts" page of an artist: uploads files from the user's PC for lost and pending posts. Each file is sent on its
// own (raw body, file name in X-File-Name); the server works out the MD5 and says whether it is the original or a custom file.
const NAME = /^(?:\[\d+\]\s*)?(\d{4,})((?:\.[a-z0-9]+)?)\.(jpe?g|png|gif|webp|webm|mp4|swf)$/i;
const PARALLEL = 3;

interface Candidate { file: File; id: number; raw: boolean; }

const root = document.getElementById("missing-upload");
if (root) {
  const url = root.dataset.url!;
  const drop = root.querySelector<HTMLElement>(".missing-drop")!;
  const summary = root.querySelector<HTMLElement>(".missing-summary")!;
  const log = root.querySelector<HTMLElement>(".missing-log")!;
  const counts = { original: 0, custom: 0, skipped: 0, failed: 0 };
  let busy = false;

  const line = (text: string, kind: string) => {
    const li = document.createElement("li");
    li.className = kind;
    li.textContent = text;
    log.append(li);
    return li;
  };

  const showSummary = (finished: boolean) => {
    summary.hidden = false;
    summary.textContent = `${counts.original} original, ${counts.custom} custom, ${counts.skipped} skipped, ${counts.failed} failed` + (finished ? "." : "...");
    if (finished && counts.original + counts.custom > 0) {
      const reload = document.createElement("a");
      reload.href = location.href;
      reload.textContent = " Reload the list";
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

  async function upload(candidate: Candidate) {
    const row = line(`${candidate.file.name}: uploading...`, "pending");
    try {
      const response = await fetch(`${url}/${candidate.id}`, { method: "POST", body: candidate.file, headers: { "X-File-Name": encodeURIComponent(candidate.file.name) } });
      const body = await response.json().catch(() => ({}));
      if (response.ok) {
        const original = body.original === true;
        counts[original ? "original" : "custom"]++;
        row.className = "ok";
        row.textContent = `${candidate.file.name}: ${original ? "original (MD5 matches)" : "custom (MD5 differs)"}, #${candidate.id}`;
      } else if (response.status === 409) {
        counts.skipped++;
        row.className = "skipped";
        row.textContent = `${candidate.file.name}: skipped, ${body.error ?? "not on the list"}`;
      } else {
        counts.failed++;
        row.className = "failed";
        row.textContent = `${candidate.file.name}: ${body.error ?? "failed (" + response.status + ")"}`;
      }
    } catch (error) {
      counts.failed++;
      row.className = "failed";
      row.textContent = `${candidate.file.name}: ${(error as Error).message}`;
    }
    showSummary(false);
  }

  async function handle(files: File[]) {
    if (busy) return;
    busy = true;
    counts.original = counts.custom = counts.skipped = counts.failed = 0;
    log.replaceChildren();

    // Names are matched by id. When an id has a ".raw" file, the others for it are left out: those were post-processed.
    const named: Candidate[] = [];
    for (const file of files) {
      const match = NAME.exec(file.name);
      if (!match) { counts.skipped++; line(`${file.name}: skipped, the name has no post id`, "skipped"); continue; }
      named.push({ file, id: Number(match[1]), raw: match[2].toLowerCase() === ".raw" });
    }
    const rawIds = new Set(named.filter((c) => c.raw).map((c) => c.id));
    const queue: Candidate[] = [];
    const seen = new Set<string>();
    for (const c of named) {
      if (!c.raw && rawIds.has(c.id)) { counts.skipped++; line(`${c.file.name}: skipped, a .raw file is used for #${c.id}`, "skipped"); continue; }
      const key = `${c.id}:${c.raw}`;
      if (seen.has(key)) { counts.skipped++; line(`${c.file.name}: skipped, another file for #${c.id} is already being added`, "skipped"); continue; }
      seen.add(key);
      queue.push(c);
    }

    showSummary(false);
    const next = async () => { for (let c = queue.shift(); c; c = queue.shift()) await upload(c); };
    await Promise.all(Array.from({ length: Math.min(PARALLEL, queue.length) }, next));
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
