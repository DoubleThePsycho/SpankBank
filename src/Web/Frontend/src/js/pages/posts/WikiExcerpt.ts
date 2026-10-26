// Ported from e621ng post_search.js initialize_wiki_preview (BSD-2, see THIRD-PARTY-NOTICES.md): the collapsible block above the posts.

const KEY = "sb-wiki-excerpt"; // "0" closed, "1" open, "2" dismissed

function stored (): string | null {
  try { return localStorage.getItem(KEY); } catch { return null; }
}

function store (value: string): void {
  try { localStorage.setItem(KEY, value); } catch { /* not remembered */ }
}

$(() => {
  document.querySelectorAll<HTMLElement>(".wiki-excerpt").forEach((excerpt) => {
    let open = stored() === "1";
    if (stored() === "2") return; // dismissed: stays hidden
    excerpt.classList.toggle("open", open);
    excerpt.classList.remove("hidden");
    window.setTimeout(() => excerpt.classList.remove("loading"), 250); // no rollout animation on first paint

    excerpt.querySelector(".wiki-excerpt-toggle")?.addEventListener("click", (event) => {
      event.preventDefault();
      open = !open;
      excerpt.classList.toggle("open", open);
      store(open ? "1" : "0");
    });

    excerpt.querySelector(".wiki-excerpt-dismiss")?.addEventListener("click", (event) => {
      event.preventDefault();
      event.stopPropagation(); // the button sits inside the toggle header
      excerpt.classList.add("hidden");
      store("2");
    });
  });
});
