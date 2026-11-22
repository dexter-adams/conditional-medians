// Builds the game UI module (ConditionalMedians2.mjs and .css) that the game
// loads from the mod's folder, after the Cities: Skylines II UI mod template.
// The packages come from ./node_modules (npm ci) or, without one, from the
// folder named by MEDIANS_NODE_MODULES, so an existing template install can be
// reused offline.
const path = require("path");
const fs = require("fs");
const MOD = require("./mod.json");

const LOCAL_MODULES = path.resolve(__dirname, "node_modules");
const NODE_MODULES = fs.existsSync(LOCAL_MODULES) ? LOCAL_MODULES : process.env.MEDIANS_NODE_MODULES;
if (!NODE_MODULES || !fs.existsSync(NODE_MODULES)) {
  throw "No node_modules: run npm ci in CS2/UI, or set MEDIANS_NODE_MODULES to a UI template's node_modules folder.";
}
const load = (name) => require(path.join(NODE_MODULES, name));
const MiniCssExtractPlugin = load("mini-css-extract-plugin");
const TerserPlugin = load("terser-webpack-plugin");
const { CSSPresencePlugin } = require("./tools/css-presence");

const OUTPUT_DIR = process.env.MEDIANS_UI_OUTPUT || path.resolve(__dirname, "dist");

const banner = `
 * Cities: Skylines II UI Module
 *
 * Id: ${MOD.id}
 * Author: ${MOD.author}
 * Version: ${MOD.version}
 * Dependencies: ${MOD.dependencies.join(",")}
`;

module.exports = {
  mode: "production",
  stats: "errors-warnings",
  context: __dirname,
  entry: {
    [MOD.id]: "./src/index.tsx",
  },
  externalsType: "window",
  externals: {
    react: "React",
    "react-dom": "ReactDOM",
    "cs2/modding": "cs2/modding",
    "cs2/api": "cs2/api",
    "cs2/bindings": "cs2/bindings",
    "cs2/l10n": "cs2/l10n",
    "cs2/ui": "cs2/ui",
    "cs2/input": "cs2/input",
    "cs2/utils": "cs2/utils",
    "cohtml/cohtml": "cohtml/cohtml",
  },
  module: {
    rules: [
      {
        test: /\.tsx?$/,
        // Types are checked in the editor against ./types; the build only transpiles.
        use: { loader: "ts-loader", options: { transpileOnly: true } },
        exclude: /node_modules/,
      },
      {
        test: /\.s?css$/,
        include: path.join(__dirname, "src"),
        use: [
          MiniCssExtractPlugin.loader,
          {
            loader: "css-loader",
            options: {
              url: true,
              importLoaders: 1,
              modules: {
                auto: true,
                exportLocalsConvention: "camelCase",
                localIdentName: "[local]_[hash:base64:3]",
              },
            },
          },
          "sass-loader",
        ],
      },
    ],
  },
  resolve: {
    extensions: [".tsx", ".ts", ".js"],
    modules: [NODE_MODULES, path.join(__dirname, "src")],
    alias: {
      "mod.json": path.resolve(__dirname, "mod.json"),
    },
  },
  resolveLoader: {
    modules: [NODE_MODULES],
  },
  output: {
    path: OUTPUT_DIR,
    library: {
      type: "module",
    },
    publicPath: `coui://ui-mods/`,
  },
  optimization: {
    minimize: true,
    minimizer: [
      new TerserPlugin({
        extractComments: {
          banner: () => banner,
        },
      }),
    ],
  },
  experiments: {
    outputModule: true,
  },
  plugins: [new MiniCssExtractPlugin(), new CSSPresencePlugin()],
};
