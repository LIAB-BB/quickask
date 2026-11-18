// Mock DeepSeek Search API for QuickAsk's --searchtest.
import http from 'node:http'

const scores = [2.4, 1.8, 1.2, 0.8, 2.9, 1.6, 0.9, 2.1]

http.createServer((req, res) => {
  let body = ''
  req.on('data', (chunk) => { body += chunk })
  req.on('end', () => {
    console.log(`[search] ${req.method} ${req.url} auth=${req.headers.authorization ? 'yes' : 'no'} body=${body}`)
    if (req.method !== 'POST' || req.url !== '/v1/tools/search') {
      res.writeHead(404).end()
      return
    }
    let parsed = {}
    try { parsed = JSON.parse(body || '{}') } catch { }
    const count = Math.min(Number(parsed.count) || 8, 8)
    const results = []
    for (let i = 0; i < count; i++) {
      results.push({
        title: `结果 ${i + 1}：${parsed.query}`,
        url: `https://example.com/${i + 1}`,
        snippet: `短摘要 ${i + 1}`,
        summary: `这是关于「${parsed.query}」的第 ${i + 1} 条检索摘要，用于验证排序、过滤与引用。`,
        date: '2026-11-16T00:00:00+00:00',
        rerank_score: scores[i % scores.length],
      })
    }
    res.writeHead(200, { 'content-type': 'application/json' })
    res.end(JSON.stringify({ results, request_id: 'mock' }))
  })
}).listen(18081, '127.0.0.1', () => console.log('[search] listening on 18081'))
