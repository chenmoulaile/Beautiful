import * as UP from 'file:///C:/Users/chenk/.dsh/profiles/desktop/node_modules/dsh-our-free-model/src/upstream.js'
import { HttpsProxyAgent } from 'file:///C:/Users/chenk/.dsh/profiles/desktop/node_modules/@earendil-works/pi-ai/node_modules/https-proxy-agent/dist/index.js'
import https from 'node:https'

const raw = process.argv[2] ?? 'none'
const PROXY = raw === 'none' ? '' : raw
const MODEL = process.argv[3] ?? 'deepseek-v4-flash-free'

const session = UP.mintSessionId()
const headers = UP.gatewayHeaders({ session, stream: false, accept: 'application/json' })
const target = UP.UPSTREAM_BASE + UP.endpointFor(MODEL)
const wire = UP.wireFor(MODEL)

const body = { model: MODEL, max_tokens: 64, stream: false, messages: [{ role: 'user', content: 'reply with the single word: pong' }] }
UP.applyFingerprint(body, wire === 'responses' ? true : wire === 'messages' ? 'claude' : false)
const payload = JSON.stringify(body)
const u = new URL(target)

function post() {
  return new Promise((resolve, reject) => {
    const opts = { method: 'POST', headers: { ...headers, 'content-length': Buffer.byteLength(payload) } }
    if (PROXY) opts.agent = new HttpsProxyAgent(PROXY)
    const req = https.request(u, opts, res => {
      let buf = ''
      res.on('data', c => buf += c)
      res.on('end', () => resolve({ status: res.statusCode, body: buf }))
    })
    req.on('error', reject)
    req.setTimeout(45000, () => req.destroy(new Error('timeout')))
    req.end(payload)
  })
}

const label = PROXY || 'DIRECT'
const started = Date.now()
try {
  const r = await post()
  console.log(`[${label}] ${MODEL} -> ${r.status} in ${Date.now() - started}ms`)
  console.log('body:', r.body.slice(0, 700).replace(/\s+/g, ' '))
} catch (e) {
  console.log(`[${label}] ${MODEL} -> FAIL in ${Date.now() - started}ms: ${e.name}: ${e.message} / cause=${e.cause?.message ?? e.cause?.code ?? 'none'}`)
}
