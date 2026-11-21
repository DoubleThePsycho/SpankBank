// Ported from e621ng PR 2572 (BSD-2, see THIRD-PARTY-NOTICES.md): favorite folders, adapted to SpankBank.
import FavoriteFolders from "@/pages/posts/FavoriteFolders";

/** Pixels of pointer movement required before a click becomes a drag. */
const ACTIVATION_THRESHOLD = 6;

/** Ghost size relative to the real thumbnail - kept within the requested 60-75% range. */
const GHOST_SCALE = 0.7;

/** Fixed pixel offset from the pointer, so the ghost never sits exactly under the cursor tip. */
const GHOST_OFFSET = 12;

// Edge autoscroll, after the pool-ordering Sortable (e621ng PR 2771, BSD-2): holding the pointer near the top or bottom
// of the viewport scrolls the page, so a post far down a long page can be dropped on a folder at the top.
/** Distance from the top/bottom viewport edge at which a drag starts scrolling the page. */
const AUTOSCROLL_EDGE = 60;
/** Autoscroll speed at the very edge of the viewport, in pixels per second. */
const AUTOSCROLL_MAX_SPEED = 1200;
/** Longest frame gap used for an autoscroll step, so a stalled frame can't cause a jump. */
const AUTOSCROLL_MAX_FRAME = 50;

/** How long a completed drag's click suppression is kept if the browser never fires the click (touch). */
const CLICK_SUPPRESSION_MS = 400;

/** lucide "move" icon: the handle that lets a finger start a drag without giving up taps and scrolling. */
const MOVE_ICON = '<svg viewBox="0 0 24 24" width="16" height="16" fill="none" stroke="currentColor" stroke-width="2" '
  + 'stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M12 2v20M2 12h20M15 19l-3 3-3-3M19 9l3 3-3 3M9 5l3-3 3 3M5 9l-3 3 3 3"/></svg>';

// Post sources use [data-id] and move via Favorite.move; folder sources use
// [data-folder-drag-source] and move via FavoriteFolder.move. Go Up and New Folder carry
// neither, so they're never drag sources.
type DragSourceKind = "post" | "folder";

interface DragState {
  pointerId: number;
  sourceEl: HTMLElement;
  captureEl: HTMLElement;
  kind: DragSourceKind;
  sourceId: string; // a post id (kind: "post") or a folder id (kind: "folder")
  startX: number;
  startY: number;
  active: boolean; // true once the movement threshold has been crossed
  currentTarget: HTMLElement | null;
  rafId: number;
  pendingMove: { clientX: number; clientY: number } | null;
  ghost: HTMLElement | null;
  /** Latest pointer position, for the drop target and the autoscroll. */
  lastPointer: { clientX: number; clientY: number };
  scrollRafId: number;
  scrollLastTs: number;
}

// Elements whose pointer sequence ended in a completed (threshold-crossing) drag get a
// one-shot entry here, so the synthetic click the browser still fires after pointerup can
// be caught and suppressed - a movement threshold alone only stops a *drag* from starting
// on a small jitter, it does nothing about the click a real, completed drag still
// generates on release.
const suppressedClickTargets = new WeakSet<Element>();

/**
 * Favorites click-and-drag: posts move via FavoriteFolders.movePost, folders via
 * FavoriteFolders.moveFolder (parent_id only, never sibling order). A mouse or pen drags the whole card (after a small
 * movement); a finger drags by the move handle in the card's corner (shown on touch screens), so taps and scrolling keep
 * working. The card being moved stays in its grid slot as a dashed placeholder while its copy follows the pointer.
 *
 * Not built on the generic Sortable utility: it starts dragging on pointerdown (breaking
 * click navigation) and only handles reordering within one container, not distinct drop
 * zones.
 */
export default class FavoriteFolderDrag {
  container: HTMLElement;
  drag: DragState | null = null;

  constructor (container: Element) {
    this.container = container as HTMLElement;
    this.container.addEventListener("pointerdown", this.onPointerDown);
    // Capture phase: must run before the thumbnail link's own click/navigation handling.
    this.container.addEventListener("click", this.onClickCapture, true);
    // Native HTML5 drag on the image/link would hijack the pointer stream before our own
    // threshold logic ever runs, so suppress it for drag sources unconditionally.
    this.container.addEventListener("dragstart", this.onNativeDragStart);
    this.addMoveHandles();
  }

  /** Puts a move handle in the corner of every card that can be dragged (styled only on touch screens, see favorites.scss). */
  addMoveHandles (): void {
    for (const card of this.container.querySelectorAll<HTMLElement>("article.thumbnail[data-id], article.thumbnail[data-folder-drag-source]")) {
      const handle = document.createElement("span");
      handle.className = "favorite-move-handle";
      handle.setAttribute("aria-hidden", "true");
      handle.title = "Drag to move";
      handle.innerHTML = MOVE_ICON;
      card.appendChild(handle);
    }
  }

  onNativeDragStart = (event: DragEvent): void => {
    const target = event.target as Element;
    if (target.closest("article.thumbnail[data-id], article.thumbnail[data-folder-drag-source]")) {
      event.preventDefault();
    }
  };

  onPointerDown = (event: PointerEvent): void => {
    if (event.button !== 0) return;
    if (this.drag) return;

    const target = event.target as Element;
    // A finger only drags by the handle, and starts at once: the handle opts out of scrolling (touch-action), the rest of
    // the card keeps tapping and scrolling as usual.
    const touch = event.pointerType === "touch";
    if (touch && !target.closest(".favorite-move-handle")) return;

    const postSourceEl = target.closest<HTMLElement>("article.thumbnail[data-id]");
    if (postSourceEl) {
      if (!this.container.contains(postSourceEl)) return;
      const postId = postSourceEl.dataset.id;
      if (!postId) return;
      this.beginDrag(event, "post", postId, postSourceEl, postSourceEl);
      if (touch) this.activate(event);
      return;
    }

    const folderSourceEl = target.closest<HTMLElement>("article.thumbnail[data-folder-drag-source]");
    if (folderSourceEl) {
      if (!this.container.contains(folderSourceEl)) return;
      // Rename/delete live in the footer; only the main body/link area starts a drag.
      if (target.closest(".favorite-folder-card-desc")) return;
      const folderId = folderSourceEl.dataset.folderId;
      if (!folderId) return;
      this.beginDrag(event, "folder", folderId, folderSourceEl, folderSourceEl);
      if (touch) this.activate(event);
      return;
    }
  };

  beginDrag (event: PointerEvent, kind: DragSourceKind, sourceId: string, sourceEl: HTMLElement, captureEl: HTMLElement): void {
    this.drag = {
      pointerId: event.pointerId,
      sourceEl,
      captureEl,
      kind,
      sourceId,
      startX: event.clientX,
      startY: event.clientY,
      active: false,
      currentTarget: null,
      rafId: 0,
      pendingMove: null,
      ghost: null,
      lastPointer: { clientX: event.clientX, clientY: event.clientY },
      scrollRafId: 0,
      scrollLastTs: 0,
    };

    // Bound at the document level, not sourceEl: no pointer capture is held yet, so a
    // fast flick can move the pointer off sourceEl before threshold is reached, and a
    // listener on sourceEl alone would then never fire, leaving `this.drag` stuck.
    document.addEventListener("pointermove", this.onPreActivationPointerMove);
    document.addEventListener("pointerup", this.onPreActivationPointerUp);
    document.addEventListener("pointercancel", this.onPreActivationPointerCancel);
  }

  onPreActivationPointerMove = (event: PointerEvent): void => {
    const drag = this.drag;
    if (!drag || drag.active || event.pointerId !== drag.pointerId) return;

    const distance = Math.hypot(event.clientX - drag.startX, event.clientY - drag.startY);
    if (distance < ACTIVATION_THRESHOLD) return;
    this.activate(event);
  };

  onPreActivationPointerUp = (event: PointerEvent): void => {
    const drag = this.drag;
    if (!drag || drag.active || event.pointerId !== drag.pointerId) return;
    // Below threshold: nothing was ever touched (no preventDefault, no capture), so the
    // browser's normal click/navigation for this pointer sequence proceeds untouched.
    this.removePreActivationListeners();
    this.drag = null;
  };

  onPreActivationPointerCancel = (event: PointerEvent): void => {
    const drag = this.drag;
    if (!drag || drag.active || event.pointerId !== drag.pointerId) return;
    this.removePreActivationListeners();
    this.drag = null;
  };

  removePreActivationListeners (): void {
    document.removeEventListener("pointermove", this.onPreActivationPointerMove);
    document.removeEventListener("pointerup", this.onPreActivationPointerUp);
    document.removeEventListener("pointercancel", this.onPreActivationPointerCancel);
  }

  activate (event: PointerEvent): void {
    const drag = this.drag;
    if (!drag) return;

    this.removePreActivationListeners();

    drag.active = true;
    event.preventDefault();
    drag.captureEl.setPointerCapture(drag.pointerId);
    // Shown as a dashed placeholder in its slot ("this is the one being moved"); stays in the grid until commitMove
    // confirms success, so a cancelled/failed drag never reflows sibling cards.
    drag.sourceEl.classList.add("favorite-dragging");
    drag.ghost = this.createGhost(drag, event.clientX, event.clientY);

    drag.captureEl.addEventListener("pointermove", this.onPointerMove);
    drag.captureEl.addEventListener("pointerup", this.onPointerUp);
    drag.captureEl.addEventListener("pointercancel", this.onPointerCancel);
    drag.lastPointer = { clientX: event.clientX, clientY: event.clientY };
    this.startAutoScroll();
  }

  createGhost (drag: DragState, clientX: number, clientY: number): HTMLElement {
    const ghost = drag.kind === "folder"
      ? this.createFolderGhost(drag)
      : this.createPostGhost(drag);

    const rect = drag.sourceEl.getBoundingClientRect();
    Object.assign(ghost.style, {
      position: "fixed",
      left: `${clientX + GHOST_OFFSET}px`,
      top: `${clientY + GHOST_OFFSET}px`,
      width: `${rect.width * GHOST_SCALE}px`,
      height: `${rect.height * GHOST_SCALE}px`,
      margin: "0",
      pointerEvents: "none",
      zIndex: "9999",
    });
    document.body.appendChild(ghost);
    return ghost;
  }

  createPostGhost (drag: DragState): HTMLElement {
    const ghost = drag.sourceEl.cloneNode(true) as HTMLElement;
    ghost.classList.remove("favorite-dragging");
    ghost.classList.add("favorite-drag-ghost");
    ghost.querySelector(".favorite-move-handle")?.remove();
    this.sanitizeGhost(ghost);
    return ghost;
  }

  // Folder-card styles are scoped under #c-favorites, so a clone would render broken
  // under document.body - build a minimal icon+name ghost instead (favorites.scss).
  createFolderGhost (drag: DragState): HTMLElement {
    const ghost = document.createElement("article");
    ghost.className = "favorite-drag-ghost favorite-folder-drag-ghost";

    const sourceIcon = drag.sourceEl.querySelector(".folder-icon-closed");
    if (sourceIcon) {
      const iconWrapper = document.createElement("div");
      iconWrapper.className = "favorite-folder-drag-ghost-icon";
      const iconClone = sourceIcon.cloneNode(true) as HTMLElement;
      iconClone.removeAttribute("class");
      this.sanitizeGhost(iconClone);
      iconWrapper.appendChild(iconClone);
      ghost.appendChild(iconWrapper);
    }

    const desc = document.createElement("div");
    desc.className = "favorite-folder-drag-ghost-desc";
    desc.textContent = drag.sourceEl.dataset.folderName || "";
    ghost.appendChild(desc);

    return ghost;
  }

  // Strips id/data-* from the element and descendants so the ghost can never match a
  // real drag-source/drop-target selector or collide with a real element id.
  sanitizeGhost (ghost: HTMLElement): void {
    const nodes = [ghost, ...Array.from(ghost.querySelectorAll<HTMLElement>("*"))];
    for (const node of nodes) {
      node.removeAttribute("id");
      for (const attr of Array.from(node.attributes)) {
        if (attr.name.startsWith("data-")) node.removeAttribute(attr.name);
      }
    }
  }

  onPointerMove = (event: PointerEvent): void => {
    const drag = this.drag;
    if (!drag || event.pointerId !== drag.pointerId) return;

    if (drag.ghost) {
      drag.ghost.style.left = `${event.clientX + GHOST_OFFSET}px`;
      drag.ghost.style.top = `${event.clientY + GHOST_OFFSET}px`;
    }

    drag.pendingMove = { clientX: event.clientX, clientY: event.clientY };
    drag.lastPointer = drag.pendingMove;
    this.startAutoScroll();
    if (!drag.rafId) {
      drag.rafId = requestAnimationFrame(() => {
        drag.rafId = 0;
        const move = drag.pendingMove;
        drag.pendingMove = null;
        if (move) this.updateHoverTarget(move.clientX, move.clientY);
      });
    }
  };

  /**
   * Scroll speed for a pointer at the given viewport Y, in pixels per second: negative near the top, positive near the
   * bottom, 0 elsewhere. Ramps linearly from 0 at the inner edge of the zone to the maximum at the viewport edge.
   */
  autoScrollSpeed (clientY: number): number {
    const edge = Math.min(AUTOSCROLL_EDGE, window.innerHeight / 4);
    if (edge <= 0) return 0;

    // A captured pointer can report positions outside the viewport; treat those as being at the edge.
    const y = Math.min(Math.max(clientY, 0), window.innerHeight);
    if (y < edge) return -AUTOSCROLL_MAX_SPEED * ((edge - y) / edge);
    const fromBottom = window.innerHeight - y;
    if (fromBottom < edge) return AUTOSCROLL_MAX_SPEED * ((edge - fromBottom) / edge);
    return 0;
  }

  startAutoScroll (): void {
    const drag = this.drag;
    if (!drag || drag.scrollRafId || this.autoScrollSpeed(drag.lastPointer.clientY) === 0) return;

    drag.scrollLastTs = 0;
    drag.scrollRafId = requestAnimationFrame((timestamp) => this.autoScrollStep(timestamp));
  }

  autoScrollStep (timestamp: number): void {
    const drag = this.drag;
    if (!drag) return;
    drag.scrollRafId = 0;

    const speed = this.autoScrollSpeed(drag.lastPointer.clientY);
    if (speed === 0) {
      drag.scrollLastTs = 0;
      return;
    }

    // First frame of a run has no previous timestamp; assume a typical frame.
    const elapsed = drag.scrollLastTs ? timestamp - drag.scrollLastTs : 16;
    drag.scrollLastTs = timestamp;
    const dt = Math.min(Math.max(elapsed, 0), AUTOSCROLL_MAX_FRAME) / 1000;

    const before = window.scrollY;
    window.scrollBy(0, speed * dt);
    if (window.scrollY === before) {
      // Top or bottom of the page; resumes on the next pointermove into the zone.
      drag.scrollLastTs = 0;
      return;
    }

    // The page moved under a pointer that may be still, so work out what is under it now.
    this.updateHoverTarget(drag.lastPointer.clientX, drag.lastPointer.clientY);
    drag.scrollRafId = requestAnimationFrame((ts) => this.autoScrollStep(ts));
  }

  updateHoverTarget (clientX: number, clientY: number): void {
    const drag = this.drag;
    if (!drag) return;

    const hit = document.elementFromPoint(clientX, clientY);
    const hovered = hit ? hit.closest<HTMLElement>("[data-drop-target]") : null;
    // Folder cards are also drop targets; exclude the source itself so dropping a folder
    // onto its own (dimmed) card is never reachable.
    const resolved = hovered && hovered !== drag.sourceEl && this.container.contains(hovered) ? hovered : null;

    if (resolved === drag.currentTarget) return;
    if (drag.currentTarget) drag.currentTarget.classList.remove("drag-hover");
    if (resolved) resolved.classList.add("drag-hover");
    drag.currentTarget = resolved;
  }

  onPointerUp = (event: PointerEvent): void => {
    const drag = this.drag;
    if (!drag || event.pointerId !== drag.pointerId) return;
    // Re-resolve synchronously from this event's coordinates rather than trusting a
    // possibly-unflushed RAF-throttled updateHoverTarget (a fast flick can release before
    // the next paint).
    this.updateHoverTarget(event.clientX, event.clientY);
    this.endDrag(false);
  };

  onPointerCancel = (event: PointerEvent): void => {
    const drag = this.drag;
    if (!drag || event.pointerId !== drag.pointerId) return;
    this.endDrag(true);
  };

  endDrag (cancelled: boolean): void {
    const drag = this.drag;
    if (!drag) return;

    drag.captureEl.removeEventListener("pointermove", this.onPointerMove);
    drag.captureEl.removeEventListener("pointerup", this.onPointerUp);
    drag.captureEl.removeEventListener("pointercancel", this.onPointerCancel);
    if (drag.rafId) cancelAnimationFrame(drag.rafId);
    if (drag.scrollRafId) cancelAnimationFrame(drag.scrollRafId);

    if (drag.currentTarget) drag.currentTarget.classList.remove("drag-hover");
    if (drag.ghost) drag.ghost.remove();
    drag.sourceEl.classList.remove("favorite-dragging");

    // A completed drag still fires a synthetic click on release; suppress it.
    if (!cancelled) {
      suppressedClickTargets.add(drag.sourceEl);
      // A finger's drag fires no click, which would leave the entry behind to swallow the next real tap.
      setTimeout(() => suppressedClickTargets.delete(drag.sourceEl), CLICK_SUPPRESSION_MS);
    }

    const dropTarget = cancelled ? null : drag.currentTarget;
    this.drag = null;

    if (!dropTarget) return;
    this.commitMove(drag, dropTarget);
  }

  commitMove (drag: DragState, dropTarget: HTMLElement): void {
    // Go Up's destination-folder-id doubles as the new parent_id for a folder drag.
    const destinationFolderId = dropTarget.dataset.dropTarget === "go-up"
      ? dropTarget.dataset.destinationFolderId || null
      : dropTarget.dataset.folderId || null;

    const destination = destinationFolderId ? Number(destinationFolderId) : null;
    const request = drag.kind === "folder"
      ? FavoriteFolders.moveFolder(Number(drag.sourceId), destination)
      : FavoriteFolders.movePost(Number(drag.sourceId), destination);

    // No optimistic removal, so nothing to roll back on failure; only remove on success.
    request
      .then(() => drag.sourceEl.remove())
      .catch((error: Error) => alert(error.message));
  }

  onClickCapture = (event: MouseEvent): void => {
    const target = event.target as Element;
    const sourceEl = target.closest("article.thumbnail[data-id], article.thumbnail[data-folder-drag-source]");
    if (sourceEl && suppressedClickTargets.has(sourceEl)) {
      suppressedClickTargets.delete(sourceEl);
      event.preventDefault();
      event.stopImmediatePropagation();
    }
  };
}

$(() => {
  const container = document.querySelector(".posts-container.favorites-view");
  if (!container || !container.querySelector("[data-drop-target]")) return; // no drop targets rendered
  new FavoriteFolderDrag(container);
});
