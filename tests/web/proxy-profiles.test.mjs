import assert from 'node:assert/strict';
import fs from 'node:fs';
import test from 'node:test';

const settingsSource = fs.readFileSync('src/web/scripts/settings.js', 'utf8');
const cssSource = fs.readFileSync('src/web/styles/app.css', 'utf8');
const storageSource = fs.readFileSync('src/ChatSheet.AddIn/Storage/Settings.cs', 'utf8');
const clientSource = fs.readFileSync('src/ChatSheet.AddIn/Providers/ChatClient.cs', 'utf8');

test('代理配置支持多套切换与自动切换', () => {
  assert.match(settingsSource, /proxyProfiles/);
  assert.match(settingsSource, /activeProxyProfileId/);
  assert.match(settingsSource, /autoSwitchProxy/);
  assert.match(settingsSource, /proxyPasswordChanges/);
  assert.match(settingsSource, /proxyPasswordChanges\.has\(activeProxyId\)/);
  assert.ok(settingsSource.includes('proxyForm.replaceChildren(renderProxySection())'));
  assert.ok(!settingsSource.includes('form.append(renderProxySection())'));
  assert.ok(settingsSource.indexOf('form.append(renderModelSection())') < settingsSource.indexOf('form.append(renderBehaviorSection())'));
  assert.match(settingsSource, /function validateProxySettings\(\)/);
  assert.match(settingsSource, /activateConnection\(\{ preserveSelection: true \}\)/);
  assert.match(settingsSource, /payload\.modelChosenForConnection = true/);
  assert.match(settingsSource, /el\('div', 'row proxy-endpoint-row'\)/);
  assert.match(cssSource, /\.proxy-endpoint-row > \.input\[type="number"\]/);
});

test('宿主保留旧单代理兼容并为候选代理轮换创建客户端', () => {
  assert.match(storageSource, /ProxyProfiles/);
  assert.match(storageSource, /ResolveProxyCandidates/);
  assert.match(storageSource, /ProxySecretKeyFor/);
  assert.match(storageSource, /passwordOverride != null/);
  assert.match(clientSource, /IReadOnlyList<ProxyOptions> proxyCandidates/);
  assert.match(clientSource, /SwitchProxy\(\)/);
});
