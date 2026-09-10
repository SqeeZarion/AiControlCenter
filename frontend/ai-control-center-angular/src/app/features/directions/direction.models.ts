export type DirectionStatus = 'Active' | 'Inactive';

export interface DirectionDto {
  id: string;
  name: string;
  code: string;
  description: string | null;
  icon: string | null;
  status: DirectionStatus;
  sortOrder: number;
  createdAt: string;
  updatedAt: string;
  archivedAt: string | null;
  version: number;
  isArchived: boolean;
}

export interface DirectionListDto {
  items: DirectionDto[];
  page: number;
  pageSize: number;
  totalCount: number;
  totalPages: number;
}
export interface DirectionContent {
  name: string;
  code: string;
  description: string | null;
  icon: string | null;
}
export interface CreateDirectionRequest extends DirectionContent {
  status: DirectionStatus;
  sortOrder: number;
}
export interface UpdateDirectionRequest extends DirectionContent {
  status: DirectionStatus;
  sortOrder: number;
  version: number;
}
export interface DirectionFilters {
  includeArchived: boolean;
  status?: DirectionStatus;
  search?: string;
  page: number;
  pageSize: number;
}
