import { useRef, useState } from "react";
import type { LocalisationBox, SegmentInput } from "./api";
import { type PointerPoint, normalizeBox, pointerToOriginal } from "./workbench";

export function VisualBoxEditor({
  segment,
  onBox,
  onError,
}: {
  segment: SegmentInput;
  onBox: (box: LocalisationBox) => void;
  onError: (message: string | null) => void;
}) {
  const [natural, setNatural] = useState<{ width: number; height: number } | null>(null);
  const [dragStart, setDragStart] = useState<PointerPoint | null>(null);
  const [draftBox, setDraftBox] = useState<LocalisationBox | null>(null);
  const frameRef = useRef<HTMLDivElement>(null);
  const dragContext: VisualBoxDragContext = {
    frameRef,
    natural,
    dragStart,
    setDragStart,
    setDraftBox,
    onBox,
    onError,
  };
  const shownBox = draftBox ?? segment.box;
  if (!segment.frame_image_data_url) {
    return <div className="token-usage">Upload a frame to enable Scene visual context and the sign box editor.</div>;
  }
  return (
    <>
      <div
        ref={frameRef}
        className="visual-editor"
        role="application"
        aria-label="Draw sign box"
        onPointerDown={(event) => startBoxDrag(event, dragContext)}
        onPointerMove={(event) => moveBoxDraft(event, dragContext)}
        onPointerUp={(event) => finishBoxDrag(event, dragContext)}
      >
        <img src={segment.frame_image_data_url} alt={`Frame for ${segment.id}`} draggable={false}
          onLoad={(e) => setNatural({ width: e.currentTarget.naturalWidth, height: e.currentTarget.naturalHeight })} />
        {shownBox && natural && (
          <div className="visual-box" style={{
            left: `${(shownBox.x1 / natural.width) * 100}%`,
            top: `${(shownBox.y1 / natural.height) * 100}%`,
            width: `${((shownBox.x2 - shownBox.x1) / natural.width) * 100}%`,
            height: `${((shownBox.y2 - shownBox.y1) / natural.height) * 100}%`,
          }} />
        )}
      </div>
      <div className="token-usage">
        Drag on the frame to draw the sign box. Coordinates use original pixels; x2/y2 are exclusive.
        {segment.box ? ` Current: (${segment.box.x1},${segment.box.y1})–(${segment.box.x2},${segment.box.y2}).` : " No box selected."}
      </div>
    </>
  );
}

type FrameElementRef = React.RefObject<HTMLDivElement | null>;
type FrameSize = { width: number; height: number };
type VisualBoxDragContext = {
  frameRef: FrameElementRef;
  natural: FrameSize | null;
  dragStart: PointerPoint | null;
  setDragStart: React.Dispatch<React.SetStateAction<PointerPoint | null>>;
  setDraftBox: React.Dispatch<React.SetStateAction<LocalisationBox | null>>;
  onBox: (box: LocalisationBox) => void;
  onError: (message: string | null) => void;
};

function pointFromEvent(event: React.PointerEvent<HTMLDivElement>, context: VisualBoxDragContext): PointerPoint | null {
  const rect = context.frameRef.current?.getBoundingClientRect();
  if (!rect || !context.natural) return null;
  return pointerToOriginal(event.clientX, event.clientY, rect, context.natural);
}

function startBoxDrag(event: React.PointerEvent<HTMLDivElement>, context: VisualBoxDragContext): void {
  const point = pointFromEvent(event, context);
  if (!point) return;
  context.setDragStart(point);
  context.setDraftBox(null);
}

function moveBoxDraft(event: React.PointerEvent<HTMLDivElement>, context: VisualBoxDragContext): void {
  if (!context.dragStart || !context.natural) return;
  const point = pointFromEvent(event, context);
  if (!point) return;
  context.setDraftBox(normalizeBox(context.dragStart, point, context.natural));
}

function finishBoxDrag(event: React.PointerEvent<HTMLDivElement>, context: VisualBoxDragContext): void {
  if (!context.dragStart || !context.natural) return;
  const point = pointFromEvent(event, context);
  const box = point ? normalizeBox(context.dragStart, point, context.natural) : null;
  context.setDragStart(null);
  context.setDraftBox(null);
  if (!box) {
    context.onError("Sign box must be at least 8×8 original pixels.");
    return;
  }
  context.onBox(box);
}
