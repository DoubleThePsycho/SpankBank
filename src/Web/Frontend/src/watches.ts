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
  }
});

// Confirm before destructive forms (ajax or not).
document.addEventListener("submit", (event) => {
  const message = (event.target as HTMLElement).dataset.confirm;
  if (message && !confirm(message)) event.preventDefault();
});

// <a data-submit> inside a form acts as its submit button.
document.addEventListener("click", (event) => {
  const link = (event.target as HTMLElement).closest<HTMLAnchorElement>("a[data-submit]");
  if (!link) return;
  event.preventDefault();
  link.closest("form")?.requestSubmit();
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
      document.querySelector(`tr[data-open-modal="${overlay?.id}"]`)?.remove();
      overlay?.remove();
      if (!document.querySelector("tr[data-open-modal]")) location.reload();
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

// Copies the values in the body's data-summary onto the matching cells of the list row.
function updateRow (overlay: HTMLElement, slot: HTMLElement) {
  const summary = slot.querySelector<HTMLElement>("[data-summary]");
  const row = document.querySelector(`tr[data-open-modal="${overlay.id}"]`);
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
