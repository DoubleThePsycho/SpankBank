import Provider from "@/components/autocomplete/Provider";
import { TagItem } from "@/components/autocomplete/Types";
import TagProvider from "./TagProvider";

// SpankBank addition: artist tag autocomplete for a multi-line field with one artist per line.
// It completes the line the cursor is on and leaves the other lines alone.
export default class ArtistListProvider extends TagProvider {
  private static lineBounds (input: HTMLInputElement | HTMLTextAreaElement): { start: number; end: number } {
    const caret = input.selectionStart ?? input.value.length;
    const start = input.value.lastIndexOf("\n", caret - 1) + 1;
    const next = input.value.indexOf("\n", caret);
    return { start, end: next === -1 ? input.value.length : next };
  }

  public async search (_query: string, input: HTMLInputElement) {
    const { start, end } = ArtistListProvider.lineBounds(input);
    // Several names on one line separated by commas: complete the last one.
    const line = input.value.slice(start, end).split(",").pop() ?? "";
    return Provider.clampSearchResults<TagItem>(line, (term) => TagProvider.findTags(term, 1));
  }

  public insert (input: HTMLInputElement, completion: string) {
    const { start, end } = ArtistListProvider.lineBounds(input);
    const line = input.value.slice(start, end);
    const comma = line.lastIndexOf(",");
    const keep = comma === -1 ? "" : line.slice(0, comma + 1) + " ";
    const replacement = keep + completion;
    input.value = input.value.slice(0, start) + replacement + input.value.slice(end);
    input.selectionStart = input.selectionEnd = start + replacement.length;
    input.dispatchEvent(new Event("input", { bubbles: true }));
  }
}
