if (db.adminCommand({hello:1}).setName !== 'et-local-rs') throw new Error('Only the isolated local replica set is allowed');
const roles = db.getSiblingDB('ET_LOCAL_ACCEPTANCE_DR_TEST').getCollection('ET.Server.Role');
const id=Long('910001');
if(process.env.ET_FIXTURE_MUTATION === 'damage') {
    if(roles.updateOne({_id:id},{$inc:{PositionX:1}}).modifiedCount!==1) throw new Error('Fixture missing');
} else if(process.env.ET_FIXTURE_MUTATION === 'repair') {
    const original=db.getSiblingDB('ET_LOCAL_ACCEPTANCE').getCollection('ET.Server.Role').findOne({_id:id});
    if(!original) throw new Error('Fixture missing');
    if(roles.replaceOne({_id:id},original).matchedCount!==1) throw new Error('Restore fixture missing');
} else {throw new Error('Explicit fixture mutation action required');}
