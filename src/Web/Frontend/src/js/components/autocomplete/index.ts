// Adapted from e621ng: no CurrentUser setting gate, and only the providers SpankBank serves data for.
import AutocompleteWidget from "@/components/autocomplete/AutocompleteWidget";
import Logger from "@/utility/Logger";

import Provider from "@/components/autocomplete/Provider";
import ArtistTagProvider from "@/components/autocomplete/providers/ArtistTagProvider";
import CharacterTagProvider from "@/components/autocomplete/providers/CharacterTagProvider";
import TagProvider from "@/components/autocomplete/providers/TagProvider";
import TagQueryProvider from "@/components/autocomplete/providers/TagQueryProvider";

const PROVIDERS: Record<string, new () => Provider> = {
  "tag-query": TagQueryProvider,
  "tag-edit": TagQueryProvider,
  "tag": TagProvider,
  "artist-tag": ArtistTagProvider,
  "character-tag": CharacterTagProvider,
};

export default class Autocomplete {

  private static instances = new Map<Element, AutocompleteWidget>();
  private static Logger = new Logger("Autocomplete");

  public static initialize_all () {
    const match_counts: Record<string, number> = {};
    for (const type of Object.keys(PROVIDERS)) {
      const count = Autocomplete.initialize_autocomplete(type);
      if (count > 0) match_counts[type] = count;
    }

    const message = [`Initialized with ${Object.keys(match_counts).length} providers:`];
    for (const [type, count] of Object.entries(match_counts)) message.push(` - ${type}: ${count}`);
    Autocomplete.Logger.log(message.join("\n"));
  }

  /**
   * Initializes autocomplete widgets on all input fields with the specified data-autocomplete type.
   * @param type The data-autocomplete type to initialize
   */
  public static initialize_autocomplete (type: string): number {
    const ProviderClass = PROVIDERS[type];
    if (!ProviderClass) {
      console.error(`No provider found for type: ${type}`);
      return 0;
    }

    let count = 0;
    const fields = document.querySelectorAll(`[data-autocomplete="${type}"]`);

    fields.forEach(field => {
      if (Autocomplete.instances.has(field))
        Autocomplete.instances.get(field).destroy();

      const instance = new AutocompleteWidget(field as HTMLInputElement, new ProviderClass());
      Autocomplete.instances.set(field, instance);
      count++;
    });
    return count;
  }
}

document.addEventListener("DOMContentLoaded", () => {
  Autocomplete.initialize_all();
});
