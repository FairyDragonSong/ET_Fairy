// Keep credentials out of process arguments; the child inherits only its environment.
let connectionUri = process.env.ET_MONGO_URI;
if (!connectionUri) throw new Error('ET_MONGO_URI is required');
if (!/[?&]serverSelectionTimeoutMS=/i.test(connectionUri)) {
    connectionUri += (connectionUri.includes('?') ? '&' : '?') + 'serverSelectionTimeoutMS=30000';
}
if (process.env.ET_MONGO_CA_FILE) {
    connectionUri += (connectionUri.includes('?') ? '&' : '?') + 'tlsCAFile=' + encodeURIComponent(process.env.ET_MONGO_CA_FILE);
}
db = new Mongo(connectionUri).getDB('admin');
