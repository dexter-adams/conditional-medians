import { Panel } from "cs2/ui";
import { useValue } from "cs2/api";
import { closedCount$, hoverDetail$, hoverState$, hoverTitle$, status$, toggleTool, toolActive$ } from "./bindings";
import styles from "./medians.module.scss";

// What a click on the hovered junction does, and the colour of its marker,
// matching the outline the tool draws on the median opening.
const actions: Record<string, { hint: string; tone: string }> = {
  Closable: { hint: "Click to close the median opening.", tone: styles.toneClose },
  Closed: { hint: "Click to reopen the median.", tone: styles.toneReopen },
  Draining: { hint: "Click to reopen the median.", tone: styles.toneReopen },
  Updating: { hint: "Click to reopen the median.", tone: styles.toneReopen },
  NotDrawn: { hint: "Click to reopen the median.", tone: styles.toneReopen },
  Refused: { hint: "", tone: styles.toneRefused },
  NotJunction: { hint: "", tone: styles.toneNeutral },
  NoOpening: { hint: "", tone: styles.toneNeutral },
};

// The tool's panel, shown while the median tool is on.
export const MedianPanel = () => {
  const active = useValue(toolActive$);
  const state = useValue(hoverState$);
  const title = useValue(hoverTitle$);
  const detail = useValue(hoverDetail$);
  const status = useValue(status$);
  const closed = useValue(closedCount$);
  if (!active) return null;
  const action = actions[state];
  return (
    // Opens at the left, clear of the junctions in the middle of the view; it can be dragged by its header.
    <Panel className={styles.panel} contentClassName={styles.content} draggable initialPosition={{ x: 0.16, y: 0.35 }}
      header="Conditional Medians" onClose={toggleTool}>
      <div className={styles.intro}>
        Hover a junction on a road with a built-in or painted median. Click to close its median opening, or to reopen it.
        Blue: can be closed. Orange: closed, click to reopen. Red: cannot be closed yet.
      </div>
      <div className={styles.label}>Junction</div>
      {action ? (
        <div className={styles.junction}>
          <div className={styles.titleRow}>
            <div className={`${styles.marker} ${action.tone}`} />
            <div className={styles.title}>{title}</div>
          </div>
          {detail && <div className={styles.detail}>{detail}</div>}
          {action.hint && <div className={styles.hint}>{action.hint}</div>}
        </div>
      ) : (
        <div className={styles.idle}>No junction under the cursor.</div>
      )}
      {status && (
        <>
          <div className={styles.label}>Last change</div>
          <div className={styles.status}>{status}</div>
        </>
      )}
      <div className={styles.footer}>
        {closed === 1 ? "1 closed median in this city." : `${closed} closed medians in this city.`} Right-click to exit.
      </div>
    </Panel>
  );
};
