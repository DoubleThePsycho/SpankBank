// Ported from e621ng PR 2572 (BSD-2, see THIRD-PARTY-NOTICES.md): favorite folders, adapted to SpankBank (one user, no CSRF token, own endpoints).
import FavoriteFolders from "@/pages/posts/FavoriteFolders";

/** Modal for creating and renaming a folder, built on the shared .st-overlay classes (common/overlay_modal.scss). */
export default class FavoriteFolderOverlay {
  private backdrop = document.createElement("div");
  private heading = document.createElement("h1");
  private form = document.createElement("form");
  private input = document.createElement("input");
  private submit = document.createElement("button");
  private error = document.createElement("div");

  private folderId: string | null = null;
  private parentId: string | null = null;
  private submitting = false;
  private trigger: HTMLElement | null = null;

  constructor () {
    this.backdrop.className = "st-overlay hidden";
    this.backdrop.setAttribute("role", "dialog");
    this.backdrop.setAttribute("aria-modal", "true");
    this.backdrop.tabIndex = -1;
    this.backdrop.addEventListener("mousedown", (event) => { if (event.target === this.backdrop) this.close(); });
    this.backdrop.addEventListener("keydown", (event) => { if (event.key === "Escape") this.close(); });

    const wrapper = document.createElement("div");
    wrapper.className = "st-overlay-wrapper favorite-folder-overlay-wrapper";

    const closeButton = document.createElement("button");
    closeButton.type = "button";
    closeButton.className = "st-button close-button";
    closeButton.innerHTML = "&times;";
    closeButton.setAttribute("aria-label", "Close");
    closeButton.addEventListener("click", () => this.close());

    this.heading.id = "favorite-folder-overlay-title";
    this.backdrop.setAttribute("aria-labelledby", this.heading.id);

    this.form.className = "simple_form";
    this.form.addEventListener("submit", this.onSubmit);

    const field = document.createElement("div");
    field.className = "input";
    const label = document.createElement("label");
    label.htmlFor = "favorite-folder-name";
    label.textContent = "Folder name";
    this.input.id = "favorite-folder-name";
    this.input.type = "text";
    this.input.required = true;
    this.input.maxLength = 100;
    field.append(label, this.input);

    this.submit.type = "submit";
    this.submit.className = "st-button submit";
    this.submit.textContent = "Save";
    this.error.className = "st-overlay-error";

    this.form.append(this.heading, field, this.submit, this.error);
    wrapper.append(closeButton, this.form);
    this.backdrop.appendChild(wrapper);
    document.body.appendChild(this.backdrop);
  }

  openForCreate (parentId: string | null, trigger: HTMLElement): void {
    this.folderId = null;
    this.parentId = parentId;
    this.heading.textContent = "New Folder";
    this.input.value = "";
    this.show(trigger);
  }

  openForRename (folderId: string, name: string, trigger: HTMLElement): void {
    this.folderId = folderId;
    this.parentId = null;
    this.heading.textContent = "Rename Folder";
    this.input.value = name;
    this.show(trigger);
    this.input.select();
  }

  close (): void {
    if (this.submitting || this.backdrop.classList.contains("hidden")) return;
    this.backdrop.classList.add("hidden");
    this.trigger?.focus();
  }

  private show (trigger: HTMLElement): void {
    this.trigger = trigger;
    this.submitting = false;
    this.submit.disabled = false;
    this.error.textContent = "";
    this.backdrop.classList.remove("hidden");
    this.input.focus();
  }

  private onSubmit = (event: SubmitEvent): void => {
    event.preventDefault();
    if (this.submitting) return;

    this.submitting = true;
    this.submit.disabled = true;
    this.error.textContent = "";

    const name = this.input.value;
    const request = this.folderId ? FavoriteFolders.rename(this.folderId, name) : FavoriteFolders.create(name, this.parentId);
    request.then(() => window.location.reload()).catch((error: Error) => {
      this.submitting = false;
      this.submit.disabled = false;
      this.error.textContent = error.message || "Something went wrong.";
    });
  };
}

$(() => {
  const trigger = document.getElementById("favorite-folder-new-trigger");
  if (!trigger) return; // not the folder view of /posts/favorites

  const overlay = new FavoriteFolderOverlay();
  trigger.addEventListener("click", (event) => {
    event.preventDefault();
    overlay.openForCreate(trigger.dataset.parentId || null, trigger);
  });

  // Delegated: every folder card has its own rename and delete buttons.
  document.addEventListener("click", (event) => {
    const target = event.target as Element;

    const rename = target.closest<HTMLElement>(".favorite-folder-rename-trigger");
    if (rename) {
      event.preventDefault();
      overlay.openForRename(rename.dataset.folderId || "", rename.dataset.folderName || "", rename);
      return;
    }

    const remove = target.closest<HTMLElement>(".favorite-folder-delete-trigger");
    if (remove) {
      event.preventDefault();
      if (!confirm(`Delete the folder "${remove.dataset.folderName}"? Its favorites and folders move up one level.`)) return;
      FavoriteFolders.remove(remove.dataset.folderId || "")
        .then(() => window.location.reload())
        .catch((error: Error) => alert(error.message));
    }
  });
});
