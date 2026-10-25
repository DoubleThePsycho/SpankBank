import Provider from "@/components/autocomplete/Provider";
import { TagItem } from "@/components/autocomplete/Types";
import TagProvider from "./TagProvider";

// SpankBank addition: the tag autocomplete, limited to character tags (category 4).
export default class CharacterTagProvider extends TagProvider {
  public async search (query: string) {
    return Provider.clampSearchResults<TagItem>(query, (term) => TagProvider.findTags(term, 4));
  }
}
