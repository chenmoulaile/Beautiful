import { postKiloStreamed } from 'file:///C:/Users/chenk/.dsh/profiles/desktop/node_modules/dsh-our-free-model/src/kilo.js'
// onData delivers RAW STRINGS, not parsed objects - that is why the first probe printed empty text.
for (const id of ['nvidia/nemotron-3.5-super-vl:free','kilo-auto/free','reflection/beam-free','liquid/lfm-2.5-2.6b:free','dots-studio/dots-3-note-preview:free']) {
  const t = Date.now()
  let content = [], reasoning = [], finish = null
  try {
    await postKiloStreamed({
      body: { model: id, max_tokens: 256, stream: true, messages: [{ role: 'user', content: 'What is 17 plus 25? Reply with just the number.' }] },
      onData: raw => {
        let d; try { d = typeof raw === 'string' ? JSON.parse(raw) : raw } catch { return }
        const c = d?.choices?.[0]
        if (c?.delta?.content) content.push(c.delta.content)
        if (c?.delta?.reasoning) reasoning.push(c.delta.reasoning)
        if (c?.finish_reason) finish = c.finish_reason
      },
      timeoutMs: 90000,
    })
    console.log(`${id}\n  OK ${Date.now() - t}ms finish=${finish} answer=${JSON.stringify(content.join('').slice(0,140))} reasoningChars=${reasoning.join('').length}`)
  } catch (e) {
    console.log(`${id}\n  FAIL ${Date.now() - t}ms [${e.code ?? ''}] ${e.message.slice(0,160)}`)
  }
}
