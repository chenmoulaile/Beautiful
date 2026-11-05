import { fetchKiloListing, postKiloStreamed } from 'file:///C:/Users/chenk/.dsh/profiles/desktop/node_modules/dsh-our-free-model/src/kilo.js'

const listing = await fetchKiloListing({ timeoutMs: 20000 }).catch(e => { console.log('listing FAIL:', e.code ?? '', e.message); return null })
if (!listing) process.exit(0)

const rows = listing.data ?? []
const free = rows.filter(m => m?.id && /:free|(^|\/)free$|-free$/i.test(String(m.id)))
console.log(`listing OK: ${rows.length} entries, ${free.length} free`)

const pick = free.slice(0, 6)
for (const m of pick) {
  const id = m.id
  const t = Date.now()
  let chunks = [], done = null
  try {
    await postKiloStreamed({
      body: { model: id, max_tokens: 48, stream: true, messages: [{ role: 'user', content: 'reply with the single word: pong' }] },
      onData: d => { if (d?.choices?.[0]?.delta?.content) chunks.push(d.choices[0].delta.content); if (d?.choices?.[0]?.finish_reason) done = d.choices[0].finish_reason },
      timeoutMs: 60000,
    })
    console.log(`  ${id.padEnd(52)} -> OK ${Date.now() - t}ms finish=${done} :: ${chunks.join('').slice(0, 90)}`)
  } catch (e) {
    console.log(`  ${id.padEnd(52)} -> FAIL ${Date.now() - t}ms [${e.code ?? ''}] ${e.message.slice(0, 140)}`)
  }
}
