// Minimal OpenAI-compatible SSE mock for QuickAsk's --selftest.
import http from 'node:http'

const port = Number(process.argv[2] || 18080)
const reply = ['你', '好', '，', '自', '检', '通', '过', '。']

http.createServer((req, res) => {
  if (req.method !== 'POST') {
    res.writeHead(404).end()
    return
  }
  let body = ''
  req.on('data', (chunk) => { body += chunk })
  req.on('end', () => {
    console.log(`[mock] ${req.method} ${req.url} model=${safeModel(body)}`)
    res.writeHead(200, {
      'content-type': 'text/event-stream',
      'cache-control': 'no-cache',
      connection: 'keep-alive',
    })
    let i = 0
    const timer = setInterval(() => {
      if (i < reply.length) {
        res.write(`data: ${JSON.stringify({ choices: [{ delta: { content: reply[i] } }] })}\n\n`)
        i += 1
      } else {
        res.write('data: [DONE]\n\n')
        clearInterval(timer)
        res.end()
      }
    }, 25)
  })
}).listen(port, '127.0.0.1', () => {
  console.log(`[mock] listening on http://127.0.0.1:${port}`)
})

function safeModel(body) {
  try { return JSON.parse(body).model } catch { return '?' }
}
