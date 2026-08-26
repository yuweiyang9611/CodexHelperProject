'use strict';

const fs = require('node:fs');

const lockPath = process.argv[2];
if (!lockPath) {
  throw new Error('Expected a package-lock.json path.');
}

const lock = JSON.parse(fs.readFileSync(lockPath, 'utf8'));
process.stdout.write(JSON.stringify({
  lockVersion: lock.version ?? null,
  rootVersion: lock.packages?.['']?.version ?? null,
}));
