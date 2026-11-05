// Why does jsdelivr serve feed/manifest.json but fail every vendor/ file?
// The manifest's base is "../" — so jsdelivr resolves vendor/* to the REPO ROOT,
// while the release files actually live under the release tree. Check both roots.
const base = 'https://cdn.jsdelivr.net/gh/zouyuxuan122/dsh-our-free-model@main'
const probes = [
  `${base}/vendor/channel-pack/NOTICE.md`,
  `${base}/feed/../vendor/channel-pack/NOTICE.md`,
  `${base}/packages/standalone/vendor/channel-pack/NOTICE.md`,
]
for (const url of probes) {
  const t = Date.now()
  try {
    const r = await fetch(url, { redirect: 'error', signal: AbortSignal.timeout(20000) })
    const b = Buffer.from(await r.arrayBuffer())
    console.log(`OK   ${r.status} ${b.length}B ${Date.now() - t}ms  ${url}`)
  } catch (e) {
    console.log(`FAIL ${e.name} "${e.message}" cause=${JSON.stringify(e.cause)} ${Date.now() - t}ms`)
    console.log(`     ${url}`)
  }
}