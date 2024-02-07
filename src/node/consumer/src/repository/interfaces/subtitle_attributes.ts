import {Optional} from "sequelize";

export interface ISubtitleAttributes {
    infoHash: string;
    fileIndex: number;
    fileId?: number;
    title: string;
    path: string;
}

export interface ISubtitleCreationAttributes extends Optional<ISubtitleAttributes, 'fileId'> {
}