// Synthetic transport fixture derived from the frozen AppTT bytecode, not live service data.
// Also consumed by the original-plugin oracle to detect different contract assumptions.
const http = require('http');
const crypto = require('crypto');
const salt = '7gp0bnd2sr85ydii2j32pcypscoc4w6c7g5spl';
const item = {vod_id: 7, vod_name: '中文电影', vod_pic: '', vod_pic_thumb: 'https://cdn.example/cover.jpg', vod_remarks: '全集', vod_year: '2026', vod_area: '大陆', vod_actor: '演员', vod_director: '导演', vod_content: '&lt;p&gt;剧情&lt;/p&gt;'};
http.createServer(async (req, res) => {
  const route = req.url.split('?')[0];
  const pathname = route.startsWith('/v3/') ? route : '/v3' + route;
  const reply = (status, value) => { res.writeHead(status, {'content-type': 'application/json'}); res.end(JSON.stringify(value)); };
  if (pathname === '/v3/parse') return reply(200, {data: {url: 'https://media.example/parsed.mp4', header: {'Referer': 'https://web.example/'}}});
  if (pathname === '/v3/health') return reply(200, {ready: true});
  let raw = '';
  // Interception supplies req.body; fall back to the stream when run directly with Node.
  if (req.body !== undefined) raw = typeof req.body === 'string' ? req.body : JSON.stringify(req.body);
  else for await (const chunk of req) raw += chunk;
  const fields = {};
  for (const match of raw.matchAll(/name=(?:"([^"]*)"|([^;\r\n]*))[^\r\n]*\r\n(?:[^\r\n]+\r\n)*\r\n([\s\S]*?)\r\n--/g)) fields[match[1] ?? match[2]] = match[3];
  if (!fields.timestamp && req.body && typeof req.body === 'object') Object.assign(fields, req.body);
  const expected = crypto.createHash('md5').update(salt + fields.timestamp).digest('hex');
  if (req.method !== 'POST' || req.headers['user-agent'] !== 'okhttp-okgo/jeasonlzy' || fields.sign !== expected)
    return reply(400, {error: 'multipart, UA or signature mismatch', fields});
  switch (pathname) {
    case '/v3/type/top_type': return reply(200, {data: {list: [{type_id: 1, type_name: '电影'}]}});
    case '/v3/type/tj_vod': return reply(200, {data: {type_vod: [{vod: [item]}]}});
    case '/v3/home/type_search':
      if (fields.limit !== '12' || !fields.type_id || !fields.page) return reply(400, {error: 'category contract'});
      return reply(200, {data: {list: Number(fields.page) < 3 ? [item] : []}});
    case '/v3/home/search':
      if (!fields.keyword || fields.page || fields.limit) return reply(400, {error: 'search contract'});
      return reply(200, {data: {list: [item, {...item, vod_id: 8, vod_name: '其他'}]}});
    case '/v3/home/vod_details':
      if (fields.vod_id !== '7') return reply(404, {error: 'unknown id'});
      return reply(200, {data: {...item, vod_play_list: [
        {flag: 'direct', parse_urls: [], urls: [{name: '第一集', url: 'https://media.example/direct.mp4'}]},
        {flag: 'json', parse_urls: [' ', 'null', `http://${req.headers.host}/v3/parse?url=`], urls: [{name: '正片', url: 'https://web.example/watch/7'}]}
      ]}});
    default: return reply(501, {error: 'route outside this fixture'});
  }
}).listen(process.env.PORT || 4901, '127.0.0.1');
