// The search box grows with its text, and Enter runs the search instead of adding a line (Shift+Enter still does).
// Behavior follows e621ng post_search.js initialize_input (BSD-2, see THIRD-PARTY-NOTICES.md).

$(() => {
  document.querySelectorAll<HTMLTextAreaElement>(".post-search textarea[name='tags']").forEach((textarea) => {
    const resize = (): void => {
      textarea.style.height = "0";
      textarea.style.height = `${textarea.scrollHeight}px`;
    };

    textarea.addEventListener("input", resize);
    window.addEventListener("resize", resize);
    textarea.addEventListener("keypress", (event) => {
      if (event.key !== "Enter" || event.shiftKey) return;
      event.preventDefault();
      textarea.closest("form")?.requestSubmit();
    });
    resize();
  });
});
