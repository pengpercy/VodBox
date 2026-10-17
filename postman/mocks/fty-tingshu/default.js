// Synthetic transport fixture reconstructed from Tingshu275 bytecode and guard 0x10fe6c.
const http = require('http');
const crypto = require('crypto');
const md5 = value => crypto.createHash('md5').update(value).digest('hex');
const salt = 'J9gSpfUlzYxE8Hn5IXiGaD2jVMrwAm0K';
const book = {id: '7', bookTitle: '中文小说', bookImage: 'https://cdn.example/book.jpg', bookAnchor: '播音', categoryName: '玄幻奇幻', bookDesc: '简介'};
http.createServer((req, res) => {
  const url = new URL(req.url, 'http://localhost');
  const reply = (status, value) => { res.writeHead(status, {'content-type': 'application/json'}); res.end(JSON.stringify(value)); };
  if (req.method !== 'GET' || req.headers['user-agent'] !== 'TingShiJie/1.8.8 (m.i275.com)') return reply(400, {error: 'method/UA'});
  switch (url.pathname) {
    case '/listen/Apitzg2025/categoryList':
      if (url.searchParams.get('size') !== '10' || !url.searchParams.get('categoryId')) return reply(400, {error: 'list query'});
      return reply(200, {data: {list: url.searchParams.get('page') === '1' ? [book] : []}});
    case '/listen/Apitzg2025/book': return reply(200, {data: {bookData: book}});
    case '/listen/Apitzg2025/chapter':
      if (url.searchParams.get('size') !== '9999' || url.searchParams.get('sort') !== 'asc') return reply(400, {error: 'chapter query'});
      return reply(200, {data: {list: [{title: '第一章', chapterId: '42'}, {title: '第二章', chapterId: '43'}]}});
    case '/listen/Apitzg2025/AppGetChapterUrl2023': {
      const timestamp = url.searchParams.get('timeStamp');
      if (url.searchParams.get('addItParapet') !== md5(md5(timestamp + salt) + salt) || url.searchParams.get('chapterId') !== '42' || url.searchParams.get('bookId') !== '7') return reply(400, {error: 'play signature'});
      return reply(200, {src: 'https://media.example/chapter.mp3'});
    }
    case '/listen/Apitzg2025//listen/apphaidao/appSearch': return reply(200, {data: {bookData: [book]}});
    default: return reply(501, {error: 'route outside fixture'});
  }
}).listen(process.env.PORT || 4914, '127.0.0.1');
