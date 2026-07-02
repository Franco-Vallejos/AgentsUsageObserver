// Genera/actualiza web/public/releases.json a partir de los commits de un tag.
//
// Uso (local o CI):
//   node scripts/generate-changelog.mjs <version> [commitsFile] [diffFile]
//     version      -> "0.2.0" (sin la "v")
//     commitsFile  -> archivo con un mensaje de commit por línea. Si se omite,
//                     lee los commits desde STDIN (uno por línea).
//     diffFile     -> (opcional) archivo con el diff de código del rango. Se le
//                     pasa a la IA como contexto para entender QUÉ cambió de
//                     verdad, no solo lo que dicen los mensajes. Se trunca.
//
// Variables de entorno:
//   GITHUB_TOKEN / GH_TOKEN -> credencial para GitHub Models (en Actions es github.token).
//   GITHUB_MODELS_MODEL     -> modelo (default "openai/gpt-4o-mini").
//   DIFF_CHAR_LIMIT         -> máx. de caracteres de diff a enviar (default 24000).
//   RELEASE_DATE            -> fecha YYYY-MM-DD (default: hoy en UTC).
//   REPO                    -> "owner/repo" para armar la URL del instalador
//                              (default "Franco-Vallejos/AgentsUsageObserver").
//
// Si la llamada a GitHub Models falla o no devuelve JSON válido, cae a un
// fallback que arma el changelog con los mensajes de commit crudos, de modo
// que el release nunca se quede sin entrada en el JSON.

import { readFileSync, writeFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';

const __dirname = dirname(fileURLToPath(import.meta.url));
const JSON_PATH = resolve(__dirname, '..', 'web', 'public', 'releases.json');

const MODELS_ENDPOINT = 'https://models.github.ai/inference/chat/completions';
const MODEL = process.env.GITHUB_MODELS_MODEL || 'openai/gpt-4o-mini';
const REPO = process.env.REPO || 'Franco-Vallejos/AgentsUsageObserver';
const DIFF_CHAR_LIMIT = Number(process.env.DIFF_CHAR_LIMIT) || 24000;
const VALID_TYPES = new Set(['new', 'fix', 'change']);

function readCommits(commitsFile) {
  const raw = commitsFile
    ? readFileSync(commitsFile, 'utf8')
    : readFileSync(0, 'utf8'); // STDIN
  return raw
    .split('\n')
    .map((l) => l.trim())
    .filter(Boolean)
    // Descarta commits de mantenimiento típicos del propio pipeline.
    .filter((l) => !/^chore\(release\)|^Update releases\.json/i.test(l));
}

// Lee y recorta el diff: descarta ruido (lockfiles, binarios, artefactos de
// build) y trunca al límite de caracteres para no reventar la ventana del modelo.
function readDiff(diffFile) {
  if (!diffFile) return '';
  let raw;
  try {
    raw = readFileSync(diffFile, 'utf8');
  } catch {
    return '';
  }

  // Trocea por archivo ("diff --git ...") y filtra los que no aportan al changelog.
  const blocks = raw.split(/^(?=diff --git )/m);
  const noise = /(package-lock\.json|pnpm-lock\.yaml|yarn\.lock|\.min\.(js|css)$|web\/dist\/|installer_output\/|\.(png|ico|jpg|jpeg|gif|exe|dll|pdb)\b)/i;
  const kept = blocks.filter((b) => b.trim() && !noise.test(b.split('\n', 1)[0]));

  let diff = kept.join('');
  if (diff.length > DIFF_CHAR_LIMIT) {
    diff = diff.slice(0, DIFF_CHAR_LIMIT) + '\n\n[... diff truncado ...]';
  }
  return diff;
}

async function generateWithAI(version, commits, diff) {
  const token = process.env.GITHUB_TOKEN || process.env.GH_TOKEN;
  if (!token) throw new Error('Sin GITHUB_TOKEN: se usa el fallback.');

  const system =
    'Sos un asistente que redacta changelogs para usuarios finales de una app de escritorio, ' +
    'en español rioplatense, claros y sin jerga técnica. Agrupá y resumí; no repitas commits casi idénticos. ' +
    'Cuando se te dé el diff de código, usalo para entender qué cambió REALMENTE y describir el impacto ' +
    'para el usuario, no los detalles internos. Los mensajes de commit son una guía; el diff manda.';
  const user =
    `Generá el changelog de la versión ${version}.\n` +
    `Devolvé EXCLUSIVAMENTE un objeto JSON con la forma ` +
    `{"changes":[{"type":"new|fix|change","text":"..."}]}. ` +
    `"new" = funcionalidad nueva, "fix" = arreglo de bug, "change" = cambio/mejora de algo existente.\n\n` +
    `Mensajes de commit:\n${commits.map((c) => `- ${c}`).join('\n')}` +
    (diff
      ? `\n\nDiff de código del rango (para entender el cambio real; ignorá el ruido):\n\`\`\`diff\n${diff}\n\`\`\``
      : '');

  const res = await fetch(MODELS_ENDPOINT, {
    method: 'POST',
    headers: {
      Authorization: `Bearer ${token}`,
      'Content-Type': 'application/json',
      Accept: 'application/json',
    },
    body: JSON.stringify({
      model: MODEL,
      temperature: 0.2,
      response_format: { type: 'json_object' },
      messages: [
        { role: 'system', content: system },
        { role: 'user', content: user },
      ],
    }),
  });

  if (!res.ok) {
    throw new Error(`GitHub Models respondió ${res.status}: ${await res.text()}`);
  }

  const data = await res.json();
  const content = data?.choices?.[0]?.message?.content;
  if (!content) throw new Error('Respuesta de GitHub Models sin contenido.');

  const parsed = JSON.parse(content);
  const changes = Array.isArray(parsed) ? parsed : parsed.changes;
  const clean = (changes || [])
    .filter((c) => c && typeof c.text === 'string' && c.text.trim())
    .map((c) => ({
      type: VALID_TYPES.has(c.type) ? c.type : 'change',
      text: c.text.trim(),
    }));
  if (clean.length === 0) throw new Error('La IA no devolvió cambios utilizables.');
  return clean;
}

function classify(commit) {
  if (/^feat(\(|:|!)/i.test(commit)) return 'new';
  if (/^fix(\(|:|!)/i.test(commit)) return 'fix';
  return 'change';
}

async function main() {
  const version = process.argv[2];
  const commitsFile = process.argv[3];
  const diffFile = process.argv[4];
  if (!version) {
    console.error('Falta la versión. Uso: node scripts/generate-changelog.mjs <version> [commitsFile] [diffFile]');
    process.exit(1);
  }

  const commits = readCommits(commitsFile);
  if (commits.length === 0) {
    console.error('No hay commits para changelog; no se modifica el JSON.');
    process.exit(0);
  }

  const diff = readDiff(diffFile);
  if (diff) console.error(`Diff incluido como contexto (${diff.length} caracteres).`);

  let changes;
  try {
    changes = await generateWithAI(version, commits, diff);
    console.error(`Changelog generado con IA (${changes.length} entradas).`);
  } catch (err) {
    console.error(`Fallback sin IA: ${err.message}`);
    // El fallback igual respeta Conventional Commits para tipar bien.
    changes = commits.map((text) => ({ type: classify(text), text }));
  }

  const date = process.env.RELEASE_DATE || new Date().toISOString().slice(0, 10);
  const installer =
    `https://github.com/${REPO}/releases/download/v${version}/AgentUsageObserver-${version}-Setup.exe`;

  const doc = JSON.parse(readFileSync(JSON_PATH, 'utf8'));
  doc.releases = doc.releases || [];
  // Quita "current" de todas y saca cualquier entrada previa de esta misma versión.
  for (const r of doc.releases) delete r.current;
  doc.releases = doc.releases.filter((r) => r.version !== version);
  doc.releases.unshift({ version, date, current: true, installer, changes });

  writeFileSync(JSON_PATH, JSON.stringify(doc, null, 2) + '\n', 'utf8');
  console.error(`releases.json actualizado con v${version}.`);
}

main().catch((err) => {
  console.error(err);
  process.exit(1);
});
