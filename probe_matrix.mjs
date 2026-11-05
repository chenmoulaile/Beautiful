import * as UP from 'file:///C:/Users/chenk/.dsh/profiles/desktop/node_modules/dsh-our-free-model/src/upstream.js'
import { HttpsProxyAgent } from 'file:///C:/Users/chenk/.dsh/profiles/desktop/node_modules/@earendil-works/pi-ai/node_modules/https-proxy-agent/dist/index.js'
import https from 'node:https'

const raw = process.argv[2] ?? 'none'
const PROXY = raw === 'none' ? '' : raw
const MODEL = process.argv[3]
const MODE = process.argv[4] ?? 'fp'      // 'fp' | 'raw'
const UA = process.argv[5] ?? ''           // override user-agent

const session = UP.mintSessionId()
const headers = UP.gatewayHeaders({ session, stream: false, accept: 'application/json' })
if (UA) headers['user-agent'] = UA
const target = UP.UPSTREAM_BASE + UP.endpointFor(MODEL)
const wire = UP.wireFor(MODEL)

const body = { model: MODEL, max_tokens: 64, stream: false, messages: [{ role: 'user', content: 'reply with the single word: pong' }] }
if (MODE === 'fp') UP.applyFingerprint(body, wire === 'responses' ? true : wire === 'messages' ? 'claude' : false)
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

const label = `${PROXY || 'DIRECT'} tools=${MODE === 'fp' ? 'read,shell' : 'none'} ua=${UA || 'plugin-default'}`
const started = Date.now()
try {
  const r = await post()
  const tag = (r.body.match(/"type":"(\w+Error)"/) || [])[1] ?? ''
  const msg = (r.body.match(/"message":"([^"]{0,90})"/) || [])[1] ?? r.body.slice(0, 90).replace(/\s+/g, ' ')
  console.log(`[${label}] ${MODEL} -> ${r.status}${tag ? ' ' + tag : ''} (${Date.now() - started}ms) :: ${msg}`)
} catch (e) {
  console.log(`[${label}] ${MODEL} -> FAIL (${Date.now() - started}ms) ${e.message}`)
}
