// Non-destructive: downloads the 2.5.3 release into a temp dir and verifies it.
// This isolates "can we fetch the release" from "can the in-app apply swap files".
import { pathToFileURL } from 'node:url'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'

const updaterPath = 'C:\\Users\\chenk\\.dsh\\profiles\\desktop\\node_modules\\dsh-our-free-model\\src\\updater.js'
const { DEFAULT_MANIFEST_SOURCES, downloadManifest, stageRelease, verifyStaged } =
  await import(pathToFileURL(updaterPath).href)

const { manifest, source } = await downloadManifest(DEFAULT_MANIFEST_SOURCES, { timeoutMs: 20000 })
const stageDir = path.join(os.tmpdir(), 'ofm-stage-probe')
const t0 = Date.now()
try {
  const staged = await stageRelease({
    manifest,
    manifestUrl: source,
    stageDir,
    onProgress: p => { if (p.done % 20 === 0) console.log(`  ${p.done}/${p.total} ${p.file}`) },
  })
  console.log(`STAGE OK in ${Date.now() - t0}ms files=${staged.files} bytes=${staged.bytes}`)
  verifyStaged(stageDir, manifest)
  console.log('VERIFY OK — every staged file matches manifest size + sha256')
  const pkg = JSON.parse(fs.readFileSync(path.join(stageDir, 'package.json'), 'utf8'))
  console.log('staged package.json version =', pkg.version)
  console.log('stage dir kept at', stageDir)
} catch (error) {
  console.log(`STAGE FAILED after ${Date.now() - t0}ms:`, String(error?.message ?? error))
}