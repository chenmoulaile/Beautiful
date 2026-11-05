// Real upgrade of the installed plugin, driven by the plugin's own PluginUpdater.
// Fix over stock behaviour: downloadManifest() picks the first mirror that
// answers, and every file URL is then resolved relative to THAT mirror. From a
// CN egress jsdelivr answers feed/manifest.json fine but fails every file fetch
// in node's fetch, so the whole staging dies on one file. fetchFallback retries
// each file against the other mirrors.
import { pathToFileURL } from 'node:url'
import fs from 'node:fs'
import path from 'node:path'

const pkgDir = 'C:\\Users\\chenk\\.dsh\\profiles\\desktop\\node_modules\\dsh-our-free-model'
const dataDir = 'C:\\Users\\chenk\\.dsh\\our-free-model'
const u = await import(pathToFileURL(`${pkgDir}\\src\\updater.js`).href)

const settingsPath = path.join(dataDir, 'settings.json')
const settings = { get: () => JSON.parse(fs.readFileSync(settingsPath, 'utf8')) }

// Each source is <root>/feed/manifest.json; the root is what file URLs share.
const roots = u.DEFAULT_MANIFEST_SOURCES.map(src => src.slice(0, src.length - 'feed/manifest.json'.length))

const tried = new Set()
async function fetchFallback(input, init) {
  const start = typeof input === 'string' ? input : String(input.url ?? input)
  const root = roots.find(r => start.startsWith(r))
  if (root === undefined) return fetch(input, init)

  const order = [root, ...roots.filter(r => r !== root)]
  const failures = []
  for (const candidate of order) {
    const url = candidate + start.slice(root.length)
    if (tried.has(url)) continue
    try {
      return await fetch(url, init)
    } catch (error) {
      tried.add(url)
      failures.push(`${new URL(url).host} ${error.name}`)
    }
  }
  throw new Error(`all mirrors failed: ${failures.join(' | ')}`)
}

const updater = new u.PluginUpdater({
  pkgDir,
  dataDir,
  settings,
  fetchImpl: fetchFallback,
  log: m => console.log('  [log]', m),
})

const before = JSON.parse(fs.readFileSync(path.join(pkgDir, 'package.json'), 'utf8')).version
const t0 = Date.now()
try {
  const result = await updater.apply()
  console.log(`APPLY OK in ${Date.now() - t0}ms:`, JSON.stringify(result))
} catch (error) {
  console.log(`APPLY FAILED after ${Date.now() - t0}ms:`, String(error?.message ?? error))
  process.exitCode = 1
}
const after = JSON.parse(fs.readFileSync(path.join(pkgDir, 'package.json'), 'utf8')).version
console.log(`version: ${before} -> ${after}`)