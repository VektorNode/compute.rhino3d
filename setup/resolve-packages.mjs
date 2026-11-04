#!/usr/bin/env node
// Resolves packages.json for a deploy: a yak entry whose version is "newest" (any release,
// prereleases included) or "stable" becomes the exact version Yak has for that Rhino major.
// Exact because yak installs a prerelease only when pinned, and so ~/plugins/DEPLOYED can
// say what the server runs. Prints the resolved manifest; one line per package on stderr.
//
//   node resolve-packages.mjs packages.json <rhinoMajor>
import { readFileSync } from 'node:fs';

const [file, major] = process.argv.slice(2);
if (!file || !/^\d+$/.test(major ?? '')) {
	console.error('usage: resolve-packages.mjs packages.json <rhinoMajor>');
	process.exit(2);
}

// SemVer 2 precedence; build metadata ignored.
function compare(a, b) {
	const split = (v) => {
		const [core, pre] = v.split('+')[0].split(/-(.*)/s);
		return { core: core.split('.').map(Number), pre: pre ? pre.split('.') : [] };
	};
	const x = split(a);
	const y = split(b);
	for (let i = 0; i < 3; i++) if ((x.core[i] ?? 0) !== (y.core[i] ?? 0)) return (x.core[i] ?? 0) - (y.core[i] ?? 0);
	if (!x.pre.length || !y.pre.length) return y.pre.length - x.pre.length;
	for (let i = 0; i < Math.max(x.pre.length, y.pre.length); i++) {
		const p = x.pre[i];
		const q = y.pre[i];
		if (p === undefined) return -1;
		if (q === undefined) return 1;
		if (p === q) continue;
		const pn = /^\d+$/.test(p);
		const qn = /^\d+$/.test(q);
		if (pn && qn) return Number(p) - Number(q);
		if (pn !== qn) return pn ? -1 : 1;
		return p < q ? -1 : 1;
	}
	return 0;
}

async function resolve(name, policy) {
	const res = await fetch(`https://yak.rhino3d.com/versions/${encodeURIComponent(name)}`);
	if (!res.ok) throw new Error(`Yak answered ${res.status} for ${name}`);
	const versions = (await res.json()).filter(
		(v) =>
			(policy === 'newest' || !v.prerelease) &&
			v.distributions?.some((d) => d.rhino_version?.startsWith(`rh${major}_`))
	);
	if (!versions.length) throw new Error(`no ${policy} ${name} on Yak for Rhino ${major}`);
	return versions.map((v) => v.version).sort(compare).at(-1);
}

const manifest = JSON.parse(readFileSync(file, 'utf8'));
for (const entry of manifest.yak ?? []) {
	if (entry.version === 'newest' || entry.version === 'stable') {
		const policy = entry.version;
		entry.version = await resolve(entry.name, policy);
		console.error(`    ${entry.name} ${entry.version}  (${policy} on Yak)`);
	} else {
		console.error(`    ${entry.name} ${entry.version ?? 'newest release at container start'}  (pinned)`);
	}
}
process.stdout.write(`${JSON.stringify(manifest, null, 2)}\n`);
