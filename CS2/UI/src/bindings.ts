import { bindValue, trigger } from "cs2/api";
import mod from "mod.json";

// The group is the mod's id; the C# side (MedianUISystem) uses the same one.
export const toolActive$ = bindValue<boolean>(mod.id, "ToolActive", false);
// None, NotJunction, NoOpening, Refused, Closable, Draining, Updating, Closed or NotDrawn.
export const hoverState$ = bindValue<string>(mod.id, "HoverState", "None");
export const hoverTitle$ = bindValue<string>(mod.id, "HoverTitle", "");
export const hoverDetail$ = bindValue<string>(mod.id, "HoverDetail", "");
export const status$ = bindValue<string>(mod.id, "Status", "");
export const closedCount$ = bindValue<number>(mod.id, "ClosedCount", 0);
export const toggleTool = () => trigger(mod.id, "ToggleTool");
