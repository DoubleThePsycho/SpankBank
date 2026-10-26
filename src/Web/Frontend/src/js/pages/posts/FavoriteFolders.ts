// Ported from e621ng PR 2572 (BSD-2, see THIRD-PARTY-NOTICES.md): favorite folders, adapted to SpankBank (one user, no CSRF token, own endpoints).

export interface FolderResult {
  id: number;
  parentId?: number | null;
  name?: string;
}

/** Thin client for the folder endpoints. Failures reject with the server's message (a broken folder rule answers 422 {message}). */
export default class FavoriteFolders {
  static create (name: string, parentId: string | number | null): Promise<FolderResult> {
    return FavoriteFolders.send("/favorite_folders", "POST", { name, parent_id: parentId ? Number(parentId) : null });
  }

  static rename (id: string | number, name: string): Promise<FolderResult> {
    return FavoriteFolders.send(`/favorite_folders/${id}`, "PATCH", { name });
  }

  static moveFolder (id: number, parentId: number | null): Promise<FolderResult> {
    return FavoriteFolders.send(`/favorite_folders/${id}/move`, "POST", { parent_id: parentId });
  }

  static remove (id: string | number): Promise<unknown> {
    return FavoriteFolders.send(`/favorite_folders/${id}`, "DELETE");
  }

  static movePost (postId: number, folderId: number | null): Promise<unknown> {
    return FavoriteFolders.send(`/favorites/${postId}/move`, "POST", { folder_id: folderId });
  }

  private static async send (url: string, method: string, body?: Record<string, unknown>): Promise<any> {
    const response = await fetch(url, {
      method,
      headers: body ? { "Content-Type": "application/json" } : {},
      body: body ? JSON.stringify(body) : undefined,
    });

    if (!response.ok) {
      let message = `${response.status} ${response.statusText}`;
      try { message = (await response.json()).message || message; } catch { /* not JSON: keep the status text */ }
      throw new Error(message);
    }
    return response.json();
  }
}
