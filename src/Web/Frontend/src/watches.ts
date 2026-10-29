// Opens and closes the watch page modals (markup uses the shared .st-overlay classes from overlay_modal.scss).
function openModal (id: string) {
  const overlay = document.getElementById(id);
  if (!overlay) return;
  overlay.classList.remove("hidden");
  overlay.querySelector<HTMLElement>("input[type=text], textarea")?.focus();
}

function closeModal (overlay: Element) {
  overlay.classList.add("hidden");
}

document.addEventListener("click", (event) => {
  const target = event.target as HTMLElement;

  const opener = target.closest<HTMLElement>("[data-open-modal]");
  if (opener && !target.closest(".st-overlay")) {
    event.preventDefault();
    openModal(opener.dataset.openModal!);
    return;
  }

  // A row with data-href is a link to its own page (its Edit link is the only thing that opens the modal).
  const row = target.closest<HTMLElement>("tr[data-href]");
  if (row && !target.closest("a, button, input, form, .st-overlay")) {
    window.location.href = row.dataset.href!;
    return;
  }

  const overlay = target.closest(".st-overlay");
  if (target.closest("button.close-button") && overlay) closeModal(overlay);
});

// Click on the backdrop itself closes.
document.addEventListener("mousedown", (event) => {
  const target = event.target as HTMLElement;
  if (target.classList.contains("st-overlay")) closeModal(target);
});

document.addEventListener("keydown", (event) => {
  const target = event.target as HTMLElement;
  if (event.key === "Escape") {
    document.querySelectorAll(".st-overlay:not(.hidden)").forEach(closeModal);
  } else if ((event.key === "Enter" || event.key === " ") && target.matches("tr[data-open-modal]")) {
    event.preventDefault();
    openModal(target.dataset.openModal!);
  } else if (event.key === "Enter" && target.matches("tr[data-href]")) {
    window.location.href = target.dataset.href!;
  }
});

// Confirm before destructive forms (ajax or not).
document.addEventListener("submit", (event) => {
  const message = (event.target as HTMLElement).dataset.confirm;
  if (message && !confirm(message)) event.preventDefault();
});

// Removing an artist: if some of their posts are on this device, ask what to do with them first.
// "posts" in the form tells the server: "all", "exclusive" (no other watch matches them) or "" (keep them).
interface RemovalCounts { total: number; exclusive: number; removable: number; favorites: number }

document.addEventListener("submit", (event) => {
  const form = event.target as HTMLFormElement;
  if (!form.dataset.removal) return;
  if (form.dataset.approved === "1") { delete form.dataset.approved; return; }
  event.preventDefault();
  void askRemoval(form, form.dataset.removal);
});

async function askRemoval (form: HTMLFormElement, name: string): Promise<void> {
  const id = form.querySelector<HTMLInputElement>("input[name=id]")!.value;
  let counts: RemovalCounts = { total: 0, exclusive: 0, removable: 0, favorites: 0 };
  try {
    const response = await fetch(`${location.pathname}?handler=RemovalCounts&id=${encodeURIComponent(id)}`);
    if (response.ok) counts = await response.json();
  } catch { /* treated as no downloaded posts */ }

  const submit = (posts: string) => {
    form.querySelector<HTMLInputElement>("input[name=posts]")!.value = posts;
    form.dataset.approved = "1";
    form.requestSubmit();
  };

  if (counts.total === 0) {
    if (confirm(`Remove ${name}?`)) submit("");
    return;
  }

  const panel = document.createElement("div");
  panel.className = "removal-choice";
  const text = document.createElement("p");
  const kept = counts.favorites > 0 ? ` ${counts.favorites.toLocaleString()} favorite(s) are always kept.` : "";
  text.textContent = `${name} has ${counts.total.toLocaleString()} downloaded post(s). Remove them too? They go to the rubbish bin for 30 days.${kept}`;
  panel.appendChild(text);

  const add = (label: string, action: () => void, disabled = false) => {
    const button = document.createElement("button");
    button.type = "button";
    button.className = "st-button";
    button.textContent = label;
    button.disabled = disabled;
    button.addEventListener("click", () => { panel.remove(); action(); });
    panel.appendChild(button);
  };
  add(`Yes (${counts.removable.toLocaleString()} posts)`, () => submit("all"), counts.removable === 0);
  add(`Yes, owned only by ${name} (${counts.exclusive.toLocaleString()})`, () => submit("exclusive"), counts.exclusive === 0);
  add("No, keep the posts", () => submit(""));
  add("Cancel", () => { /* nothing removed */ });

  form.closest(".watch-actions")?.after(panel);
}

// <a data-submit> inside a form acts as its submit button.
document.addEventListener("click", (event) => {
  const link = (event.target as HTMLElement).closest<HTMLAnchorElement>("a[data-submit]");
  if (!link) return;
  event.preventDefault();
  link.closest("form")?.requestSubmit();
});

// An Enable/Disable pill in a list switches in place: post in the background, then flip the pill and the form's next value.
document.addEventListener("submit", async (event) => {
  const form = event.target as HTMLFormElement;
  if (!form.hasAttribute("data-pill-toggle")) return;
  event.preventDefault();

  const pill = form.querySelector<HTMLButtonElement>(".status-pill");
  const next = form.querySelector<HTMLInputElement>("input[name=enabled]");
  if (!pill || !next || pill.disabled) return;

  pill.disabled = true;
  try {
    const response = await fetch(form.action, { method: "POST", body: new FormData(form), headers: { "X-Fetch": "pill" } });
    if (!response.ok) throw new Error(`HTTP ${response.status}`);

    const nowEnabled = next.value === "true";
    pill.classList.toggle("is-enabled", nowEnabled);
    pill.classList.toggle("is-disabled", !nowEnabled);
    pill.textContent = nowEnabled ? "Enabled" : "Disabled";
    pill.title = nowEnabled ? "Click to disable" : "Click to enable";
    next.value = String(!nowEnabled);
    form.closest("tr")?.classList.toggle("is-off", !nowEnabled);
  } catch {
    pill.title = "Could not save; try again";
  } finally {
    pill.disabled = false;
  }
});

// Forms marked data-ajax post in the background; the server answers with the entry's re-rendered modal body.
document.addEventListener("submit", async (event) => {
  const form = event.target as HTMLFormElement;
  if (event.defaultPrevented || !form.hasAttribute("data-ajax")) return;
  event.preventDefault();

  const overlay = form.closest<HTMLElement>(".st-overlay");
  const error = overlay?.querySelector<HTMLElement>("[data-error]");
  if (error) error.textContent = "";

  try {
    const response = await fetch(form.action, { method: "POST", body: new FormData(form), headers: { "X-Fetch": "1" } });

    if (response.status === 204) { // entry removed
      rowFor(overlay?.id)?.remove();
      overlay?.remove();
      if (!document.querySelector("tr[data-open-modal], tr[data-modal]")) location.reload();
      return;
    }

    if (!response.ok) {
      if (error) error.textContent = await response.text() || "Something went wrong.";
      return;
    }

    const slot = overlay?.querySelector<HTMLElement>("[data-slot]");
    if (!overlay || !slot) return;
    slot.innerHTML = await response.text();
    updateRow(overlay, slot);

    // The add-character form is outside the replaced part: clear it for the next entry.
    if (form.matches(".watch-add-tag")) {
      const input = form.querySelector<HTMLInputElement>("input[name=tag]")!;
      input.value = "";
      input.dispatchEvent(new Event("input", { bubbles: true })); // closes the autocomplete list, disables Add
      input.focus();
    }
  } catch {
    if (error) error.textContent = "Could not reach the server.";
  }
});

// The list row that belongs to a modal: it opens it (data-open-modal) or links to its own page (data-modal).
function rowFor (modalId: string | undefined): Element | null {
  return modalId ? document.querySelector(`tr[data-open-modal="${modalId}"], tr[data-modal="${modalId}"]`) : null;
}

// Copies the values in the body's data-summary onto the matching cells of the list row.
function updateRow (overlay: HTMLElement, slot: HTMLElement) {
  const summary = slot.querySelector<HTMLElement>("[data-summary]");
  const row = rowFor(overlay.id);
  if (!summary || !row) return;
  for (const [field, value] of Object.entries(summary.dataset))
    row.querySelector(`[data-field="${field}"]`)?.replaceChildren(value ?? "");
}

// A rename form's Save is only enabled once the text differs from what is stored.
document.addEventListener("input", (event) => {
  const input = event.target as HTMLInputElement;
  if (!input.matches?.("input[data-original]")) return;
  const button = input.form?.querySelector<HTMLButtonElement>("button[type=submit]");
  if (button) button.disabled = input.value.trim() === (input.dataset.original ?? "");
});

// "Add" stays disabled until the field holds exactly one existing tag of the given category.
document.querySelectorAll<HTMLInputElement>("input[data-validate-tag]").forEach((input) => {
  const button = input.form?.querySelector<HTMLButtonElement>("button[type=submit]");
  if (!button) return;

  let timer: number | undefined;
  let latest = 0;
  input.addEventListener("input", () => {
    button.disabled = true;
    window.clearTimeout(timer);
    const name = input.value.trim();
    if (!name) return;

    const ticket = ++latest;
    timer = window.setTimeout(async () => {
      const params = new URLSearchParams({ name, category: input.dataset.validateTag! });
      try {
        const response = await fetch(`/tags/exists.json?${params}`);
        const data: { exists: boolean } = await response.json();
        if (ticket === latest) button.disabled = !data.exists;
      } catch {
        if (ticket === latest) button.disabled = true;
      }
    }, 200);
  });
});

// Click a column header to sort the table by that column (first click ascending, then toggling).
// Numbers sort as numbers ("1,234", "3 / 5" -> 3); "pending", "-" and empty cells always go last.
function cellValue (row: HTMLTableRowElement, column: number): string {
  const cell = row.children[column] as HTMLElement | undefined;
  return (cell?.dataset.sort ?? cell?.textContent ?? "").trim();
}

function toNumber (text: string): number | null {
  const number = parseFloat(text.replace(/,/g, ""));
  return Number.isNaN(number) ? null : number;
}

function sortTable (table: HTMLTableElement, column: number, ascending: boolean): void {
  const body = table.tBodies[0];
  if (!body) return;
  const rows = Array.from(body.rows);
  const values = rows.map((row) => cellValue(row, column));
  const numeric = values.every((value) => value === "" || value === "-" || value === "pending" || toNumber(value) !== null);

  const sorted = rows.map((row, index) => ({ row, value: values[index] }));
  sorted.sort((a, b) => {
    const aMissing = a.value === "" || a.value === "-" || a.value === "pending";
    const bMissing = b.value === "" || b.value === "-" || b.value === "pending";
    if (aMissing || bMissing) return aMissing === bMissing ? 0 : aMissing ? 1 : -1; // missing last, either direction

    const result = numeric
      ? toNumber(a.value)! - toNumber(b.value)!
      : a.value.localeCompare(b.value, undefined, { sensitivity: "base", numeric: true });
    return ascending ? result : -result;
  });

  for (const { row } of sorted) body.appendChild(row);
}

// Tables marked data-server-sorted are sorted by the server (their header cells are links); the rest sort here.
document.querySelectorAll<HTMLTableElement>("table.watches-table:not([data-server-sorted])").forEach((table) => {
  const headers = Array.from(table.tHead?.rows[0]?.cells ?? []);
  headers.forEach((header, column) => {
    if (!header.textContent?.trim()) return; // the Edit column
    header.classList.add("sortable-header");
    header.tabIndex = 0;
    header.setAttribute("role", "columnheader");

    const sort = () => {
      const ascending = header.getAttribute("aria-sort") !== "ascending";
      headers.forEach((other) => other.removeAttribute("aria-sort"));
      header.setAttribute("aria-sort", ascending ? "ascending" : "descending");
      sortTable(table, column, ascending);
    };
    header.addEventListener("click", sort);
    header.addEventListener("keydown", (event) => {
      if (event.key === "Enter" || event.key === " ") { event.preventDefault(); sort(); }
    });
  });
});

// ---- Artist page: character table ----------------------------------------------------------------
// The Enable/Disable button of a character row switches it without reloading; Remove drops one you added yourself.
const chipForm = document.querySelector<HTMLFormElement>("#chip-form");

function updateCharacterCount (): void {
  const counter = document.querySelector<HTMLElement>("[data-char-count]");
  if (!counter) return;
  const rows = document.querySelectorAll(".character-table .character-row");
  counter.textContent = `${document.querySelectorAll(".character-table .character-row.is-on").length} / ${rows.length} enabled`;
}

async function sendCharacter (handler: string, tagId: string, enabled?: boolean): Promise<boolean> {
  if (!chipForm) return false;
  const url = new URL(chipForm.action, location.href);
  url.searchParams.set("handler", handler);
  const body = new FormData(chipForm);
  body.set("tagId", tagId);
  body.set("enabled", String(enabled ?? true));
  try {
    const response = await fetch(url, { method: "POST", body, headers: { "X-Fetch": "1" } });
    return response.ok;
  } catch {
    return false;
  }
}

document.addEventListener("click", async (event) => {
  const target = event.target as HTMLElement;

  const toggle = target.closest<HTMLButtonElement>(".char-toggle[data-tag-id]");
  if (toggle && chipForm) {
    const row = toggle.closest<HTMLElement>(".character-row")!;
    const enable = !row.classList.contains("is-on");
    toggle.disabled = true;
    if (await sendCharacter("ToggleTag", toggle.dataset.tagId!, enable)) {
      row.classList.toggle("is-on", enable);
      toggle.textContent = enable ? "Disable" : "Enable";
      updateCharacterCount();
    }
    toggle.disabled = false;
    return;
  }

  const remove = target.closest<HTMLButtonElement>(".char-remove[data-tag-id]");
  if (remove && chipForm && await sendCharacter("RemoveTag", remove.dataset.tagId!)) {
    remove.closest(".character-row")?.remove();
    updateCharacterCount();
  }
});

document.querySelector<HTMLInputElement>("[data-char-filter]")?.addEventListener("input", (event) => {
  const needle = (event.target as HTMLInputElement).value.trim().toLowerCase().replace(/ /g, "_");
  document.querySelectorAll<HTMLElement>(".character-table .character-row").forEach((row) => {
    row.hidden = needle.length > 0 && !(row.dataset.name ?? "").includes(needle);
  });
});
