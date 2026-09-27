import { readFileSync } from 'node:fs';
import { fileURLToPath, pathToFileURL } from 'node:url';
import path from 'node:path';

const here = path.dirname(fileURLToPath(import.meta.url));
const baseArg = process.argv.find((arg) => arg.startsWith('--base='));
if (!baseArg) throw new Error('Usage: node test-plugin-descriptions.mjs --base=<@deepseek-ai modules directory>');
const base = path.resolve(baseArg.slice('--base='.length));
const { readPluginMeta } = await import(pathToFileURL(path.join(base, 'dsh-app-boot', 'lib', 'index.js')));
const parent = pathToFileURL(path.resolve(base, '..', '..', 'lib', 'bin.js'));
const descriptions = JSON.parse(readFileSync(path.join(here, 'plugin-descriptions.json'), 'utf8'));

const builtins = Object.keys(descriptions).filter((name) => name.startsWith('@deepseek-ai/') && name !== '@deepseek-ai/dsh-plugin-updater');
const missing = builtins.filter((name) => !readPluginMeta(name, parent)?.description?.ru);
const subpaths = [
  '@deepseek-ai/dsh-plugin-manager/tools',
  '@deepseek-ai/dsh-tool-subagent-control/list-agents',
  '@deepseek-ai/dsh-tool-subagent/model-settings',
  '@deepseek-ai/dsh-tool-cordis/host',
  '@deepseek-ai/dsh-web-app/startup',
  '@deepseek-ai/dsh-plugin-updater',
];
const errors = subpaths.filter((name) => readPluginMeta(name, parent)?.error);

if (missing.length || errors.length) {
  throw new Error(`Missing translations: ${missing.join(', ')}; metadata errors: ${errors.join(', ')}`);
}
console.log(`Built-in descriptions: ${builtins.length}/${builtins.length} Russian; subpath metadata errors: 0`);
