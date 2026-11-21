// The Settings page: sidebar navigation and search (adapted from e621ng), and the live preview of the theme choice.
import SettingsNav from "@/pages/SettingsNav";

SettingsNav.instance;

for (const select of document.querySelectorAll<HTMLSelectElement>("select[data-theme-preview]"))
  select.addEventListener("change", () => document.body.setAttribute("data-th-" + select.dataset.themePreview, select.value));
