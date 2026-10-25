// Adapted from e621ng app/javascript/src/js/core/Navigation.ts (BSD-2, see THIRD-PARTY-NOTICES.md):
// only the main-menu toggle is kept; SpankBank has no accounts, so no avatar menu.
import $ from "jquery";
import Offclick, { OffclickEntry } from "@/utility/Offclick";

let offclick: OffclickEntry | null = null;

$(() => {
  const $wrapper = $("html");

  $("#nav-toggle").on("click", (event) => {
    event.preventDefault();

    if (offclick === null)
      offclick = Offclick.register("#nav-toggle", ".nav-primary, .nav-secondary, .nav-tools, .nav-help", () => {
        $wrapper.removeClass("nav-toggled");
      });

    offclick.disabled = !offclick.disabled;
    $wrapper.toggleClass("nav-toggled", !offclick.disabled);
  });
});
