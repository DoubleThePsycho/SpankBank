import $ from "jquery";
// Copied from e621ng app/javascript/src/js/core/PaginatorFastTravel.ts (BSD-2, see THIRD-PARTY-NOTICES.md); only the jquery import was added.
export default class PaginatorFastTravel {
  private $button: JQuery<HTMLAnchorElement>;

  constructor ($button: JQuery<HTMLAnchorElement>) {
    this.$button = $button;
    this.$button.on("click", (event) => {
      event.preventDefault();

      const value = prompt("Navigate to page");
      if (!value) return;

      window.location.replace(this.$button.attr("href").replace("page=0", "page=" + value));
    });
  }
}

$(() => {
  for (const one of $<HTMLAnchorElement>("nav.pagination a.spacer").get())
    new PaginatorFastTravel($(one));
});
