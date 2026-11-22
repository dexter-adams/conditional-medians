// Adds `export const hasCSS` to the module, which tells the game to load the
// .css file beside it (from the Cities: Skylines II UI mod template).
exports.CSSPresencePlugin = class CSSPresencePlugin {
  apply(compiler) {
    const { RawSource } = compiler.webpack.sources;
    compiler.hooks.compilation.tap("CSSPresencePlugin", (compilation) => {
      compilation.hooks.processAssets.tap(
        {
          name: "CSSPresencePlugin",
          stage: compilation.PROCESS_ASSETS_STAGE_ADDITIONS,
        },
        () => {
          const hasCSS = Object.keys(compilation.assets).some((asset) => asset.endsWith(".css"));
          for (const chunk of compilation.chunks) {
            for (const file of chunk.files) {
              if (!file.endsWith(".mjs")) continue;
              const source = compilation.getAsset(file).source.source();
              const updated = source.replace("export {", `const hasCSS = ${hasCSS}; export { hasCSS, `);
              compilation.updateAsset(file, new RawSource(updated));
            }
          }
        }
      );
    });
  }
};
