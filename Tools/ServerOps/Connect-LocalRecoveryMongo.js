const recoveryLogin=process.env.ET_RECOVERY_TEST_USER,recoveryPassword=process.env.ET_RECOVERY_TEST_PASSWORD;
if(process.env.ET_MONGO_DATABASE!=='ET_LOCAL_GAMEPLAY'||!/^et_recovery_test_[a-f0-9]{32}$/.test(recoveryLogin||'')||
   !/^[a-f0-9]{64}$/i.test(recoveryPassword||'')||!process.env.ET_MONGO_CA_FILE)throw new Error('Local recovery credentials or CA missing');
const recoveryUri='mongodb://'+encodeURIComponent(recoveryLogin)+':'+encodeURIComponent(recoveryPassword)+
    '@localhost:27101,localhost:27102,localhost:27103/ET_LOCAL_GAMEPLAY?authSource=ET_LOCAL_GAMEPLAY&replicaSet=et-local-rs&tls=true&retryWrites=true&serverSelectionTimeoutMS=10000&tlsCAFile='+encodeURIComponent(process.env.ET_MONGO_CA_FILE);
try {db=new Mongo(recoveryUri).getDB('admin');}
catch {throw new Error('Restricted recovery database connection failed');}
