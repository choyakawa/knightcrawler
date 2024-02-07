import { Table, Column, Model, HasMany, DataType } from 'sequelize-typescript';
import {IIngestedTorrentAttributes, IIngestedTorrentCreationAttributes} from "../interfaces/ingested_torrent_attributes";

const indexes = [
    {
        unique: true,
        name: 'ingested_torrent_unique_source_info_hash_constraint',
        fields: ['source', 'info_hash']
    }
];

@Table({modelName: 'ingested_torrent', timestamps: true, indexes: indexes})
export class IngestedTorrent extends Model<IIngestedTorrentAttributes, IIngestedTorrentCreationAttributes> {
    @Column({ type: DataType.STRING(512) })
    declare name: string;
    
    @Column({ type: DataType.STRING(512) })
    declare source: string;
    
    @Column({ type: DataType.STRING(32) })
    declare category: string;
    
    @Column({ type: DataType.STRING(64) })
    declare info_hash: string;
    
    @Column({ type: DataType.STRING(32) })
    declare size: string;
    
    @Column({ type: DataType.INTEGER })
    declare seeders: number;
    
    @Column({ type: DataType.INTEGER })
    declare leechers: number;
    
    @Column({ type: DataType.STRING(32) })
    declare imdb: string;
    
    @Column({ type: DataType.BOOLEAN, defaultValue: false })
    declare processed: boolean;
}