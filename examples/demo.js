// QuickJS global script. Console output is sent to stderr by the host.
var demoOptions = {};
var VodBoxProvider = {
  init: function (options) { demoOptions = options; return {}; },
  categories: function () { return [{ id: 'demo', name: '协议示例' }]; },
  items: function () { return { items: [{ id: 'sample', title: 'QuickJS 插件示例', remarks: '源码生成 C ABI 宿主' }], nextCursor: null }; },
  search: function (args) { return { items: 'QuickJS 插件示例'.toLowerCase().includes((args.query || '').toLowerCase()) ? this.items().items : [] }; },
  detail: function () { return { item: this.items().items[0], description: '在配置 options.mediaUri 中填写自己的媒体地址。', playbackLines: [{ id: 'main', name: '主线路', episodes: [{ id: 'main', title: '播放' }] }] }; },
  resolvePlayback: function () { if (!demoOptions.mediaUri) throw Error('请先配置 options.mediaUri'); return { uri: demoOptions.mediaUri, title: 'QuickJS 插件示例' }; }
};
