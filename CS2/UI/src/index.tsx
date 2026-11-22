import { ModRegistrar } from "cs2/modding";
import { MedianButton } from "./MedianButton";
import { MedianPanel } from "./MedianPanel";

const register: ModRegistrar = (moduleRegistry) => {
  // The floating button beside other mods' buttons (top left), and the tool's
  // panel while the tool is on.
  moduleRegistry.append("GameTopLeft", MedianButton);
  moduleRegistry.append("Game", MedianPanel);
};

export default register;
