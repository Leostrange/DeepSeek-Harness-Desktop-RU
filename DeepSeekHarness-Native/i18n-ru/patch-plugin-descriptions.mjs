import { copyFileSync, existsSync, readFileSync, writeFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

const here = path.dirname(fileURLToPath(import.meta.url));
const baseArg = process.argv.find((arg) => arg.startsWith('--base='));
const npmRoot = baseArg
  ? path.resolve(baseArg.slice('--base='.length))
  : path.join(process.env.APPDATA || '', 'npm', 'node_modules', '@deepseek-ai', 'dsh', 'node_modules', '@deepseek-ai');
const target = path.join(npmRoot, 'dsh-app-boot', 'lib', 'index.js');
const backup = `${target}.codebuff-ru.bak`;
const start = '/* == codebuff-ru:plugin-descriptions start == */';
const end = '/* == codebuff-ru:plugin-descriptions end == */';
const oldLine = '\t\tconst description = localizedText("description", dictionaries, fallbackText(manifest?.description), "");';

export function patchPluginDescriptions(revert = false) {
  if (!existsSync(target)) throw new Error(`Plugin metadata reader not found: ${target}`);
  if (revert) {
    if (existsSync(backup)) copyFileSync(backup, target);
    return;
  }

  const descriptions = JSON.parse(readFileSync(path.join(here, 'plugin-descriptions.json'), 'utf8'));
  const map = Object.fromEntries(Object.entries(descriptions).map(([name, item]) => [name, item]));
  let source = readFileSync(target, 'utf8');
  const blockStart = source.indexOf(start);
  if (blockStart !== -1) {
    const blockEnd = source.indexOf(end, blockStart);
    if (blockEnd === -1) throw new Error('Incomplete plugin description patch');
    source = source.slice(0, blockStart - 2) + oldLine + source.slice(blockEnd + end.length);
  }
  const patchedLine = [
    `\t\t${start}`,
    `\t\tconst RU_PLUGIN_DESCRIPTIONS = ${JSON.stringify(map)};`,
    '\t\tconst originalDescription = localizedText("description", dictionaries, fallbackText(manifest?.description), "");',
    '\t\tconst ruEntry = RU_PLUGIN_DESCRIPTIONS[manifest?.name];',
    '\t\tconst description = ruEntry?.en === manifest?.description && ruEntry.ru',
    '\t\t\t? { ...(typeof originalDescription === "object" ? originalDescription : { en: originalDescription ?? "" }), ru: ruEntry.ru }',
    '\t\t\t: originalDescription;',
    `\t\t${end}`,
  ].join('\n');
  if (!source.includes(oldLine)) throw new Error('Plugin metadata reader changed; translation patch was not applied');
  if (!existsSync(backup)) copyFileSync(target, backup);
  writeFileSync(target, source.replace(oldLine, patchedLine), 'utf8');
  console.log(`Russian plugin descriptions patched: ${Object.keys(map).length}`);
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  patchPluginDescriptions(process.argv.includes('--revert'));
}
