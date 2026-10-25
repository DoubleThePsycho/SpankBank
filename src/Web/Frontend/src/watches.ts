// Fills the artist datalist from /watches?handler=Suggest as the user types.
$(() => {
  const input = document.querySelector<HTMLInputElement>("input[data-suggest-url]");
  const list = document.getElementById("artist-suggestions");
  if (!input || !list) return;

  let timer: number | undefined;
  input.addEventListener("input", () => {
    window.clearTimeout(timer);
    const term = input.value.trim();
    if (term.length < 2) return;
    timer = window.setTimeout(async () => {
      const response = await fetch(`${input.dataset.suggestUrl}&term=${encodeURIComponent(term)}`);
      if (!response.ok) return;
      const names: string[] = await response.json();
      list.replaceChildren(...names.map((name) => new Option(name)));
    }, 150);
  });
});
