import SearchControls from "@/pages/posts/SearchControls";
import SearchFilters from "@/pages/posts/SearchFilters";
import PostToolbar from "@/pages/posts/PostToolbar";
import PostModes from "@/pages/posts/PostModes";
import "@/pages/posts/WikiExcerpt";
import "@/pages/posts/SearchInput";
import "@/pages/posts/FavoriteFolderDrag";
import "@/pages/posts/FavoriteFolderOverlay";

$(() => {
  new SearchControls();
  SearchFilters.initialize();
  new PostToolbar();
  new PostModes();

  // Left and right arrow keys turn the page (e621ng does the same), unless the user is typing.
  document.addEventListener("keydown", (event) => {
    const target = event.target as HTMLElement;
    if (event.ctrlKey || event.altKey || event.metaKey || target.matches("input, textarea, select, [contenteditable]")) return;

    const link = document.querySelector<HTMLAnchorElement>(
      event.key === "ArrowLeft" ? "nav.pagination a[rel~=prev], .pool-nav li.pool-selected-true a[rel~=prev]"
        : event.key === "ArrowRight" ? "nav.pagination a[rel~=next], .pool-nav li.pool-selected-true a[rel~=next]" : "");
    if (link) window.location.href = link.href;
  });
});
