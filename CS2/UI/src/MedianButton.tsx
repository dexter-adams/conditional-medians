import { Button, Tooltip } from "cs2/ui";
import { useValue } from "cs2/api";
import { toggleTool, toolActive$ } from "./bindings";
import styles from "./medians.module.scss";

// The round blue button in the top-left row of mod buttons; it lights up while
// the median tool is on.
export const MedianButton = () => {
  const active = useValue(toolActive$);
  return (
    <Tooltip tooltip="Conditional Medians: close or reopen median openings (F8)">
      <Button variant="floating" selected={active} onSelect={toggleTool} className={styles.toggle}>
        <svg viewBox="0 0 32 32" className={styles.icon} aria-hidden="true" fill="#ffffff">
          {/* A divided road seen from above, its median closed across the junction. */}
          <rect x="3" y="2" width="2.5" height="28" rx="1" />
          <rect x="26.5" y="2" width="2.5" height="28" rx="1" />
          <rect x="13" y="2" width="6" height="28" rx="2" />
          <rect x="8.5" y="4" width="1.5" height="5" rx="0.75" />
          <rect x="8.5" y="13.5" width="1.5" height="5" rx="0.75" />
          <rect x="8.5" y="23" width="1.5" height="5" rx="0.75" />
          <rect x="22" y="4" width="1.5" height="5" rx="0.75" />
          <rect x="22" y="13.5" width="1.5" height="5" rx="0.75" />
          <rect x="22" y="23" width="1.5" height="5" rx="0.75" />
        </svg>
      </Button>
    </Tooltip>
  );
};
