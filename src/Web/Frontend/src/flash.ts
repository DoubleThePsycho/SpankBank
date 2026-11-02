// Plays the post's .swf with Ruffle, which is self-hosted in /dist/ruffle (copied by vite.config.ts).
interface RufflePlayerElement extends HTMLElement {
  load (options: { url: string }): Promise<void>;
}
interface RuffleApi { newest (): { createPlayer (): RufflePlayerElement } | null }
declare global { interface Window { RufflePlayer?: RuffleApi & { config?: Record<string, unknown> } } }

const container = document.getElementById("flash-player");

if (container) {
  // Ruffle reads its settings from this object when its script loads.
  window.RufflePlayer = window.RufflePlayer ?? {} as RuffleApi;
  window.RufflePlayer.config = {
    publicPath: "/dist/ruffle/",
    autoplay: "auto",
    unmuteOverlay: "visible",
    openUrlMode: "deny", // a movie cannot send you to another site
    showSwfDownload: false,
  };

  const script = document.createElement("script");
  script.src = "/dist/ruffle/ruffle.js";
  script.onload = () => {
    const ruffle = window.RufflePlayer?.newest();
    if (!ruffle) return fail("Ruffle did not start.");

    const player = ruffle.createPlayer();
    const width = Number(container.dataset.width) || 550;
    const height = Number(container.dataset.height) || 400;
    // As large as the page allows, like an image set to fit the window: the full column width, but never taller than the window.
    player.style.width = `min(100%, calc(85vh * ${width / height}))`;
    player.style.height = "auto"; // Ruffle defaults to 400px high; the aspect ratio sets it
    player.style.aspectRatio = `${width} / ${height}`;
    container.replaceChildren(player);
    player.load({ url: container.dataset.swf! }).catch((error) => fail(`Ruffle could not play this file (${error}).`));
  };
  script.onerror = () => fail("Ruffle could not be loaded.");
  document.head.append(script);
}

function fail (message: string): void {
  const note = document.createElement("p");
  note.textContent = `${message} You can download the file instead.`;
  container?.replaceChildren(note);
}

export {};
