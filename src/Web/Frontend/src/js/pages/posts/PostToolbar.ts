// The toolbar under the image on a post page: favorite star, image size, relationship previews.
// Behavior follows e621ng's PostsShowToolbar.js and posts.js (BSD-2, see THIRD-PARTY-NOTICES.md).

const SIZE_KEY = "sb-image-size";
const RELATIONSHIPS_KEY = "sb-show-relationships";

function stored (key: string): string | null {
  try { return localStorage.getItem(key); } catch { return null; }
}

function store (key: string, value: string): void {
  try { localStorage.setItem(key, value); } catch { /* storage unavailable: the choice is just not remembered */ }
}

export default class PostToolbar {
  private readonly container = document.querySelector<HTMLElement>("#image-container");
  private readonly image = document.querySelector<HTMLImageElement | HTMLVideoElement>("#image");
  private readonly star = document.querySelector<HTMLButtonElement>(".ptbr-favorite-button");

  constructor () {
    if (!this.container) return;
    this.initFavorite();
    this.initDelete();
    this.initSize();
    this.initRelationships();
  }

  private get postId (): string { return this.container!.dataset.postId ?? ""; }

  // --- Favorite -------------------------------------------------------------------------------

  private initFavorite (): void {
    this.star?.addEventListener("click", () => { void this.toggleFavorite(); });

    document.addEventListener("keydown", (event) => {
      const target = event.target as HTMLElement;
      if (event.key !== "f" || event.ctrlKey || event.altKey || event.metaKey || target.matches("input, textarea, select, [contenteditable]")) return;
      void this.toggleFavorite();
    });
  }

  private async toggleFavorite (): Promise<void> {
    if (!this.star || this.star.getAttribute("processing") === "true") return;
    this.star.setAttribute("processing", "true");
    try {
      const add = this.star.getAttribute("favorited") !== "true";
      const response = await fetch(`/favorites/${this.postId}`, { method: add ? "PUT" : "DELETE" });
      if (!response.ok) throw new Error(`HTTP ${response.status}`);
      this.star.setAttribute("favorited", String(add));
      this.container!.dataset.isFavorited = String(add);
    } catch (error) {
      console.error("Could not update the favorite", error);
    } finally {
      this.star.setAttribute("processing", "false");
    }
  }

  // --- Delete (only rendered when delete mode is enabled in Settings) ------------------------------

  private initDelete (): void {
    const button = document.querySelector<HTMLButtonElement>(".ptbr-delete-button");
    button?.addEventListener("click", async () => {
      if (!confirm("Move this post to the rubbish bin?")) return;
      button.disabled = true;
      try {
        const response = await fetch(`/posts/${this.postId}`, { method: "DELETE" });
        if (!response.ok) throw new Error(`HTTP ${response.status}`);
        window.location.href = "/posts";
      } catch (error) {
        button.disabled = false;
        console.error("Could not delete the post", error);
        alert("The post could not be deleted.");
      }
    });
  }

  // --- Image size -----------------------------------------------------------------------------

  private initSize (): void {
    const select = document.querySelector<HTMLSelectElement>("#image-resize-selector");
    if (!select || !this.image) return;

    const saved = stored(SIZE_KEY);
    if (saved && [...select.options].some(option => option.value === saved)) select.value = saved;
    select.addEventListener("change", () => {
      store(SIZE_KEY, select.value);
      this.applySize(select.value);
    });
    this.applySize(select.value);
  }

  private applySize (size: string): void {
    const image = this.image!;
    image.classList.remove("fit-window", "fit-window-vertical");
    if (size === "fit" || size === "large") image.classList.add("fit-window");
    else if (size === "fitv") image.classList.add("fit-window-vertical");

    if (image instanceof HTMLImageElement) {
      const wanted = size === "large" ? image.dataset.sample : image.dataset.original;
      if (wanted && !image.src.endsWith(wanted)) image.src = wanted;
    }
  }

  // --- Parent and child previews --------------------------------------------------------------

  private initRelationships (): void {
    const pairs = ["parent", "children"].map(kind => ({
      preview: document.querySelector<HTMLElement>(`#has-${kind}-relationship-preview`),
      link: document.querySelector<HTMLElement>(`#has-${kind}-relationship-preview-link`),
    })).filter(pair => pair.preview && pair.link);
    if (pairs.length === 0) return;

    const toggle = (): void => {
      for (const { preview, link } of pairs) {
        const visible = preview!.style.display === "none";
        preview!.style.display = visible ? "" : "none";
        link!.textContent = visible ? "« hide" : "show »";
      }
    };

    if (stored(RELATIONSHIPS_KEY) === "1") toggle();
    for (const { link } of pairs) {
      link!.addEventListener("click", (event) => {
        event.preventDefault();
        toggle();
        store(RELATIONSHIPS_KEY, pairs[0].preview!.style.display === "none" ? "0" : "1");
      });
    }
  }
}
