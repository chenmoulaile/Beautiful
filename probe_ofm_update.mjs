// Runs the plugin's real updater code path to surface the actual failure string.
import { pathToFileURL } from 'node:url'

const updaterPath = 'C:\\Users\\chenk\\.dsh\\profiles\\desktop\\node_modules\\dsh-our-free-model\\src\\updater.js'
const { DEFAULT_MANIFEST_SOURCES, downloadManifest, PINNED_MANIFEST_PUBLIC_KEY } =
  await import(pathToFileURL(updaterPath).href)

console.log('sources:', DEFAULT_MANIFEST_SOURCES)
console.log('key len:', PINNED_MANIFEST_PUBLIC_KEY.length)

try {
  const { manifest, source } = await downloadManifest(DEFAULT_MANIFEST_SOURCES, { timeoutMs: 20000 })
  console.log('OK via', source)
  console.log('version', manifest.version, 'files', manifest.files.length, 'base', manifest.base)
} catch (error) {
  console.log('DOWNLOAD FAILED:', String(error?.message ?? error))
  console.log(error?.stack ?? '')
}