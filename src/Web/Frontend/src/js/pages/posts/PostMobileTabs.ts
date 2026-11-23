// The phone tabs on a post page (Tags / Information). The choice goes into a cookie, so the server renders the same tab next time.
const display = document.querySelector<HTMLElement>(".post-display[data-tab-state]");
const buttons = display ? Array.from(display.querySelectorAll<HTMLButtonElement>(":scope > .tabs button[data-action]")) : [];

for (const button of buttons) {
  button.addEventListener("click", () => {
    const tab = button.dataset.action!;
    display!.dataset.tabState = tab;
    for (const other of buttons) other.setAttribute("aria-selected", String(other === button));
    document.cookie = `post_tab=${tab}; path=/; max-age=31536000; SameSite=Lax`;
  });
}
