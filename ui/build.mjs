import { copyFile, mkdir } from 'node:fs/promises';
import { build } from 'esbuild';

await mkdir('dist', { recursive: true });
await build({
  entryPoints: ['app.jsx', 'surface.css'],
  bundle: true,
  minify: true,
  format: 'esm',
  target: 'edge120',
  outdir: 'dist',
  jsx: 'automatic',
  // Use the bundled font in WebView2, where the app's CSP blocks remote stylesheets.
  alias: { '@astryxdesign/theme-neutral/fonts.css': '@fontsource-variable/inter' },
  loader: { '.woff2': 'dataurl', '.woff': 'dataurl', '.svg': 'dataurl' },
});
await copyFile('index.html', 'dist/index.html');
await copyFile('phone.html', 'dist/phone.html');
await build({
  entryPoints: ['phone.jsx'],
  bundle: true,
  minify: true,
  format: 'iife',
  target: 'es2022',
  outdir: 'dist',
  loader: { '.woff2': 'dataurl' },
});
await copyFile('app-icon.png', 'dist/app-icon.png');
await copyFile('node_modules/@fontsource-variable/inter/LICENSE', 'dist/Inter-LICENSE.txt');
await copyFile('Barlow-OFL.txt', 'dist/Barlow-OFL.txt');
