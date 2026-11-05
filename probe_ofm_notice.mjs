// The real apply() failed on vendor/channel-pack/NOTICE.md. Resolve the URL the
// way the plugin does (manifest.base = "../") and see which mirrors actually serve it.
import { pathToFileURL } from 'node:url'

const u = await import(pathToFileURL('C:/Users/chenk/.dsh/profiles/desktop/node_modules/dsh-our-free-model/src/updater.js').href)
const { manifest, source } = await u.downloadManifest(u.DEFAULT_MANIFEST_SOURCES, { timeoutMs: 20000 })
console.log('base:', manifest.base, '| source:', source)

for (const f of manifest.files.filter(x => x.path.startsWith('vendor/'))) {
  const url = u.fileUrlOf(source, manifest, f.path)
  const t = Date.now()
  try {
    const r = await fetch(url, { redirect: 'error', signal: AbortSignal.timeout(25000) })
    const b = Buffer.from(await r.arrayBuffer())
    const crypto = await import('node:crypto')
    const d = crypto.createHash('sha256').update(b).digest('hex')
    console.log(`OK  ${r.status} ${f.path} got=${b.length}B want=${f.size}B hash=${d === f.sha256} ${Date.now() - t}ms`)
    console.log(`    ${url}`)
  } catch (e) {
    console.log(`FAIL ${f.path} ${e.name} "${e.message}" cause=${e.cause?.code ?? e.cause?.message ?? ''} ${Date.now() - t}ms`)
    console.log(`    ${url}`)
  }
}