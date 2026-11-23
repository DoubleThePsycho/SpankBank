// Remembers whether each <details> block (with an id) was left open or closed, per browser, and restores it when the page loads.
// The markup's own "open" attribute is only the default for a block that has not been toggled yet.
const PREFIX = "e6.details.";

function read (id: string): string | null {
  try { return localStorage.getItem(PREFIX + id); } catch { return null; }
}

function write (id: string, open: boolean) {
  try { localStorage.setItem(PREFIX + id, open ? "open" : "closed"); } catch { /* storage blocked: the default applies */ }
}

for (const details of document.querySelectorAll<HTMLDetailsElement>("details[id]")) {
  const saved = read(details.id);
  if (saved === "open") details.open = true;
  else if (saved === "closed") details.open = false;

  // Restoring fires a toggle too; it writes the value that was just read, so nothing changes.
  details.addEventListener("toggle", () => write(details.id, details.open));
}
