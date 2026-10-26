// The Mode menu on the posts index: View (default), Favorite, Unfavorite and, when enabled, Delete.
// In any mode but View a click on a thumbnail performs the action instead of opening the post.

const MODE_KEY = "sb-mode";

function stored (): string | null {
  try { return localStorage.getItem(MODE_KEY); } catch { return null; }
}

function store (mode: string): void {
  try { localStorage.setItem(MODE_KEY, mode); } catch { /* not remembered */ }
}

export default class PostModes {
  private readonly select = document.querySelector<HTMLSelectElement>("#mode-box-mode");
  private readonly banner = document.querySelector<HTMLElement>("#delete-mode-banner");

  constructor () {
    if (!this.select) return;

    // Favorite and unfavorite are remembered; delete mode never is, so a reload is always safe.
    const saved = stored();
    if (saved === "favorite" || saved === "unfavorite") this.select.value = saved;
    this.update();

    this.select.addEventListener("change", () => {
      if (this.select!.value !== "delete") store(this.select!.value);
      this.update();
    });

    document.addEventListener("click", (event) => { void this.onClick(event); }, true);
  }

  private get mode (): string { return this.select?.value ?? "view"; }

  private update (): void {
    this.banner?.classList.toggle("hidden", this.mode !== "delete");
    document.body.dataset.mode = this.mode;
  }

  private async onClick (event: MouseEvent): Promise<void> {
    if (this.mode === "view" || event.button !== 0 || event.ctrlKey || event.metaKey || event.shiftKey) return;
    const thumbnail = (event.target as Element).closest<HTMLElement>(".posts-container article.thumbnail[data-id]");
    if (!thumbnail) return;

    event.preventDefault();
    event.stopPropagation();

    const id = thumbnail.dataset.id!;
    thumbnail.classList.add("mode-removing");
    try {
      const response = await this.request(id);
      if (!response.ok) {
        const message = await response.json().then((body) => body.message as string, () => `HTTP ${response.status}`);
        throw new Error(message);
      }
      if (this.mode === "delete") thumbnail.classList.add("mode-removed");
      else thumbnail.classList.remove("mode-removing");
      if (this.mode === "favorite") thumbnail.dataset.isFavorited = "true";
      if (this.mode === "unfavorite") thumbnail.dataset.isFavorited = "false";
    } catch (error) {
      thumbnail.classList.remove("mode-removing");
      console.error("Mode action failed", error);
      alert(error instanceof Error ? error.message : "The action failed.");
    }
  }

  private request (id: string): Promise<Response> {
    switch (this.mode) {
      case "favorite": return fetch(`/favorites/${id}`, { method: "PUT" });
      case "unfavorite": return fetch(`/favorites/${id}`, { method: "DELETE" });
      default: return fetch(`/posts/${id}`, { method: "DELETE" });
    }
  }
}
