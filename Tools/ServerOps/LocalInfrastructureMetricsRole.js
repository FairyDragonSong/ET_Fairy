const user = process.env.ET_METRICS_TEST_USER;
const password = process.env.ET_METRICS_TEST_PASSWORD;
const cleanup = process.env.ET_METRICS_TEST_CLEANUP === '1';
if (!/^[a-z0-9_]{16,80}$/.test(user || '')) throw new Error('invalid metrics test user');
if (!cleanup && (!password || password.length < 32)) throw new Error('invalid metrics test password');

const admin = db.getSiblingDB('admin');
if (cleanup) {
    if (admin.getUser(user) && !admin.dropUser(user)) throw new Error('metrics test user cleanup failed');
    if (admin.getUser(user)) throw new Error('metrics test user still exists after cleanup');
} else {
    if (admin.getUser(user)) throw new Error('metrics test user already exists');
    admin.createUser({
        user,
        pwd: password,
        roles: [
            { role: 'clusterMonitor', db: 'admin' },
            { role: 'read', db: 'local' }
        ]
    });
}
