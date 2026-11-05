// Full PluginUpdater.apply() against a THROWAWAY copy of the installed package.
// Proves backup -> stage -> install -> verify works on this machine before the
// real install is touched.
import { pathToFileURL } from 'node:url'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'

const realPkg = 'C:\\Users\\chenk\\.dsh\\profiles\\desktop\\node_modules\\dsh-our-free-model'
const u = await import(pathToFileURL(`${realPkg}\\src\\updater.js`).href)

const work = path.join(os.tmpdir(), 'ofm-apply-probe')
fs.rmSync(work, { recursive: true, force: true })
fs.mkdirSync(path.join(work, 'data'), { recursive: true })
fs.cpSync(realPkg, path.join(work, 'pkg'), { recursive: true })

const settings = { get: () => ({ updateCheckHours: 0 }) }
const updater = new u.PluginUpdater({
  pkgDir: path.join(work, 'pkg'),
  dataDir: path.join(work, 'data'),
  settings,
  log: m => console.log('  [log]', m),
})

const t0 = Date.now()
try {
  const result = await updater.apply()
  console.log(`APPLY OK in ${Date.now() - t0}ms ->`, JSON.stringify(result))
  console.log('installed version now:', u.compareVersions ? '' : '')
} catch (error) {
  console.log(`APPLY FAILED after ${Date.now() - t0}ms:`, String(error?.message ?? error))
}
console.log('status:', JSON.stringify(updater.status()))
console.log('work dir kept at', work)