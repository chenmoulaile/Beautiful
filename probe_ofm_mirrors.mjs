// Which mirrors are actually usable from this machine? The real apply() picked
// the jsdelivr manifest source, so every file URL was then jsdelivr-relative.
const urls = [
  'https://raw.githubusercontent.com/zouyuxuan122/dsh-our-free-model@HEAD/x',
  'https://raw.githubusercontent.com/zouyuxuan122/dsh-our-free-model/main/vendor/channel-pack/NOTICE.md',
  'https://cdn.jsdelivr.net/gh/zouyuxuan122/dsh-our-free-model@main/vendor/channel-pack/NOTICE.md',
  'https://cdn.jsdelivr.net/gh/zouyuxuan122/dsh-our-free-model@main/feed/manifest.json',
  'https://raw.githubusercontent.com/zouyuxuan122/dsh-our-free-model/main/feed/manifest.json',
]

for (const url of urls) {
  let line = `${url.slice(0, 72).padEnd(74)}`
  for (let i = 1; i <= 3; i += 1) {
    const t = Date.now()
    try {
      const r = await fetch(url, { redirect: 'error', signal: AbortSignal.timeout(20000) })
      await r.arrayBuffer()
      line += ` [${r.status} ${Date.now() - t}ms]`
    } catch (e) {
      line += ` [FAIL ${e.name}/${e.cause?.code ?? ''} ${Date.now() - t}ms]`
    }
  }
  console.log(line)
}